using System.Net;
using System.Net.Sockets;
using BsgoBot.Net;
using BsgoBot.Protocol;

namespace BsgoBot.Proxy;

/// <summary>
/// One message observed on the wire. <see cref="Payload"/> is the whole frame starting at
/// protocolId; the body of THIS message is the slice at <see cref="BodyOffset"/>. A single
/// frame from the client can contain several of these — see <see cref="MessageSplitter"/>.
/// </summary>
public sealed record FrameInfo(
    ProtocolId Protocol,
    ushort MsgType,
    byte[] Payload,
    int BodyOffset,
    int BodyLength,
    bool FromClient)
{
    /// <summary>A reader positioned at the first field of this message's body.</summary>
    public BgoReader Reader() => new(Payload, BodyOffset, BodyLength);
}

/// <summary>
/// Sits between bsgo.exe and the real game server.
///
///     bsgo.exe ──► GameProxy (127.0.0.1:27050) ──► real server
///
/// Every frame is forwarded byte-for-byte unmodified, so the client's own session
/// (login, catalogue, chat) is never disturbed. We only observe — and additionally
/// *inject* our own frames toward the server, which arrive indistinguishable from
/// frames the client itself sent.
/// </summary>
public sealed class GameProxy : IDisposable
{
    private string _serverHost;
    private int _serverPort;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    private NetworkStream? _toServer;
    private readonly SemaphoreSlim _serverWriteLock = new(1, 1);

    /// <summary>Identifies the upstream, for anything cached per server. Card guids only mean
    /// something on the server that issued them, so caches must not be shared across hosts.</summary>
    public string UpstreamKey => $"{_serverHost}_{_serverPort}";

    public bool ClientConnected { get; private set; }
    public long FramesFromClient { get; private set; }
    public long FramesFromServer { get; private set; }
    public long MessagesFromClient { get; private set; }
    public long MessagesFromServer { get; private set; }
    public long FramesInjected { get; private set; }
    public DateTime? SessionStartedAt { get; private set; }

    /// <summary>Raised once per decoded message, in wire order.</summary>
    public event Action<FrameInfo>? Frame;

    /// <summary>Raised when a client session begins and ends — the bot resets its world on both.</summary>
    public event Action? SessionStarted;
    public event Action? SessionEnded;

    /// <summary>Raised, before <see cref="SessionEnded"/>, only when the game client was the
    /// side that closed (or the relay itself failed) — the cases where the client process is
    /// worth autopsying. A server-side drop leaves the client alive and is not this.</summary>
    public event Action? ClientEndedSession;

    public event Action<string>? Log;

    public GameProxy(string serverHost, int serverPort)
    {
        _serverHost = serverHost;
        _serverPort = serverPort;
    }

    /// <summary>Repoints the proxy at a different server. Takes effect on the next
    /// client connection; an in-flight session is left alone.</summary>
    public void SetUpstream(string host, int port)
    {
        _serverHost = host;
        _serverPort = port;
        Log?.Invoke($"Upstream set to {host}:{port}.");
    }

    public void Start(string listenHost, int listenPort)
    {
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Parse(listenHost), listenPort);
        _listener.Start();
        Log?.Invoke($"Proxy listening on {listenHost}:{listenPort} -> {_serverHost}:{_serverPort}");
        _ = AcceptLoopAsync(_cts.Token);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await _listener!.AcceptTcpClientAsync(ct);
                Log?.Invoke("Client connected.");
                _ = HandleClientAsync(client, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log?.Invoke("Accept loop stopped: " + ex.Message); }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        client.NoDelay = true;

        using var upstream = new TcpClient { NoDelay = true };
        try
        {
            await upstream.ConnectAsync(_serverHost, _serverPort, ct);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Upstream connect to {_serverHost}:{_serverPort} failed: {ex.Message}");
            return;
        }

        var cs = client.GetStream();
        var ss = upstream.GetStream();
        _toServer = ss;
        ClientConnected = true;
        SessionStartedAt = DateTime.UtcNow;
        Log?.Invoke("Upstream connected. Relaying.");
        SessionStarted?.Invoke();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            // WHICH side ended it is the whole diagnosis, and until now the log said only
            // "Client disconnected" no matter what happened. A server-side drop and the game
            // being closed produced identical output, so every theory about why sessions end
            // was unfalsifiable. Task.WhenAny hands back the pump that finished first; that
            // pump is the side that hung up.
            var fromClientPump = PumpAsync(cs, ss, fromClient: true, linked.Token);
            var fromServerPump = PumpAsync(ss, cs, fromClient: false, linked.Token);

            // The loser keeps running until the cancel in the finally reaches it, and may fault
            // on the way down. Observe both so a late failure on the side we did not wait for
            // cannot surface as an unobserved task exception.
            Observe(fromClientPump);
            Observe(fromServerPump);

            var first = await Task.WhenAny(fromClientPump, fromServerPump);
            bool clientEndedIt = ReferenceEquals(first, fromClientPump);
            _serverEndedIt = !clientEndedIt;

            string who = clientEndedIt ? "the game client" : "the server";
            var uptime = DateTime.UtcNow - (SessionStartedAt ?? DateTime.UtcNow);

            if (first.IsFaulted)
            {
                var ex = first.Exception?.GetBaseException();
                _endReason = $"{who} errored after {uptime.TotalSeconds:F0}s: {ex?.Message}";
            }
            else
            {
                _endReason = $"{who} closed the connection after {uptime.TotalSeconds:F0}s"
                           + $" ({FramesFromClient} frames up, {FramesFromServer} down,"
                           + $" {FramesInjected} injected)";
            }
        }
        catch (Exception ex) { _endReason = "relay error: " + ex.Message; }
        finally
        {
            linked.Cancel();
            ClientConnected = false;
            _toServer = null;
            SessionStartedAt = null;
            Log?.Invoke("Session ended — " + (_endReason ?? "reason unknown") + ".");

            // Only when the server hung up, and only then: if it dropped us, it dropped us over
            // something we sent, and this is that. A client-side close is the game exiting and
            // says nothing about our traffic, so dumping it there would just be noise.
            if (_serverEndedIt)
            {
                var recent = RecentInjections();
                if (recent.Count > 0)
                {
                    Log?.Invoke($"Last {recent.Count} frame(s) we injected, oldest first — "
                              + "the last one is the prime suspect:");
                    foreach (var line in recent) Log?.Invoke("    " + line);
                }
                else
                {
                    Log?.Invoke("We had injected nothing this session, so the drop was not our traffic.");
                }
            }

            lock (_recentInjections) _recentInjections.Clear();
            _endReason = null;
            if (!_serverEndedIt) ClientEndedSession?.Invoke();
            _serverEndedIt = false;
            SessionEnded?.Invoke();
        }
    }

    /// <summary>Why the last session ended, filled in before the teardown log line.</summary>
    private string? _endReason;

    /// <summary>True when the upstream was the side that closed, which is the only case where
    /// our own outgoing traffic is a suspect.</summary>
    private bool _serverEndedIt;

    private static void Observe(Task t) =>
        t.ContinueWith(x => _ = x.Exception, TaskContinuationOptions.OnlyOnFaulted);

    private async Task PumpAsync(Stream from, Stream to, bool fromClient, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var payload = await from.ReadFrameAsync(ct);
            if (payload is null) return;                 // peer closed

            // Forward first, verbatim — observation must never delay or alter the game.
            if (fromClient)
            {
                await _serverWriteLock.WaitAsync(ct);
                try { await to.WriteFrameAsync(payload, ct); }
                finally { _serverWriteLock.Release(); }
                FramesFromClient++;
            }
            else
            {
                await to.WriteFrameAsync(payload, ct);
                FramesFromServer++;
            }

            if (payload.Length < 3) continue;

            // Everything below is OBSERVATION, and the forward above has already happened. A
            // parser that throws must therefore never be able to end the session — but it could:
            // subscribers run synchronously on this loop, so one bad read unwound all the way out
            // of the pump, through the relay's catch, and into the finally that closes both
            // sockets. The client then failed its next send with "connection aborted by the
            // software in your host machine" and dropped the player out of the game.
            //
            // A message we cannot decode is a gap in what the bot knows. It is not a reason to
            // disconnect anybody.
            try
            {
                // A client frame can hold several messages back to back. Emitting only the first
                // one is how a fire click that also carried a LockTarget went unnoticed.
                var messages = MessageSplitter.Split(payload, fromClient);

                if (fromClient) MessagesFromClient += messages.Count;
                else MessagesFromServer += messages.Count;

                foreach (var m in messages)
                {
                    // Per message, so one undecodable type does not cost us the rest of the frame.
                    try
                    {
                        Frame?.Invoke(new FrameInfo(
                            m.Protocol, m.MsgType, payload, m.BodyOffset, m.BodyLength, fromClient));
                    }
                    catch (Exception ex)
                    {
                        NoteParseFailure($"{m.Protocol}/{m.MsgType}", ex);
                    }
                }
            }
            catch (Exception ex)
            {
                NoteParseFailure($"splitting a {(fromClient ? "client" : "server")} frame", ex);
            }
        }
    }

    /// <summary>
    /// Records a parser blowing up without letting it reach the relay, and without flooding the
    /// log — a message type that fails once usually fails on every one of its kind, and thousands
    /// of identical lines would bury whatever else is going on.
    /// </summary>
    private void NoteParseFailure(string what, Exception ex)
    {
        ParseFailures++;

        lock (_parseGate)
        {
            if (!_parseFailureKinds.Add(what)) return;
        }

        Log?.Invoke($"Could not decode {what}: {ex.GetType().Name} — {ex.Message}. "
                  + "Relay is unaffected; this message type is skipped from now on in the log.");
    }

    /// <summary>Messages the bot failed to decode. Non-zero means it is flying half-blind on
    /// something, but the game itself is unaffected.</summary>
    public long ParseFailures { get; private set; }

    private readonly HashSet<string> _parseFailureKinds = [];
    private readonly Lock _parseGate = new();

    /// <summary>
    /// The last few frames we injected, newest last.
    ///
    /// When the server is the side that hangs up, the cause is something we sent, and the most
    /// recent thing we sent is the first place to look. Guessing at that from behaviour has
    /// already cost several wrong theories; the bytes settle it.
    /// </summary>
    private readonly Queue<string> _recentInjections = new();
    private const int RecentInjectionsKept = 16;

    /// <summary>Sends a frame to the server as though the client had produced it.</summary>
    public async Task InjectAsync(BgoWriter w, CancellationToken ct = default)
    {
        var ss = _toServer;
        if (ss is null) throw new InvalidOperationException("No client session to inject into.");

        var frame = w.ToFrame();
        var payload = frame.AsSpan(2).ToArray();

        lock (_recentInjections)
        {
            _recentInjections.Enqueue(Describe(payload));
            while (_recentInjections.Count > RecentInjectionsKept) _recentInjections.Dequeue();
        }

        await _serverWriteLock.WaitAsync(ct);
        try { await ss.WriteFrameAsync(payload, ct); }
        finally { _serverWriteLock.Release(); }

        FramesInjected++;
    }

    /// <summary>"19:32:58.412 Game/21 (11 bytes) 1500020000000100000045" — protocol, message
    /// type and the raw body, which is what a layout argument actually needs.</summary>
    private static string Describe(byte[] payload)
    {
        if (payload.Length < 3) return $"{DateTime.Now:HH:mm:ss.fff} <runt {payload.Length}b>";
        var protocol = (ProtocolId)payload[0];
        ushort msgType = (ushort)(payload[1] | (payload[2] << 8));
        return $"{DateTime.Now:HH:mm:ss.fff} {protocol}/{msgType} ({payload.Length}b) "
             + Convert.ToHexString(payload);
    }

    /// <summary>The injection history, oldest first. Read when a session ends badly.</summary>
    public IReadOnlyList<string> RecentInjections()
    {
        lock (_recentInjections) return _recentInjections.ToList();
    }

    public void Stop()
    {
        _cts?.Cancel();
        _listener?.Stop();
        _listener = null;
        ClientConnected = false;
    }

    public void Dispose() => Stop();
}
