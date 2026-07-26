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
            await Task.WhenAny(
                PumpAsync(cs, ss, fromClient: true, linked.Token),
                PumpAsync(ss, cs, fromClient: false, linked.Token));
        }
        catch (Exception ex) { Log?.Invoke("Relay ended: " + ex.Message); }
        finally
        {
            linked.Cancel();
            ClientConnected = false;
            _toServer = null;
            SessionStartedAt = null;
            Log?.Invoke("Client disconnected.");
            SessionEnded?.Invoke();
        }
    }

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

            // A client frame can hold several messages back to back. Emitting only the first
            // one is how a fire click that also carried a LockTarget went unnoticed.
            var messages = MessageSplitter.Split(payload, fromClient);
            if (fromClient) MessagesFromClient += messages.Count;
            else MessagesFromServer += messages.Count;

            foreach (var m in messages)
            {
                Frame?.Invoke(new FrameInfo(
                    m.Protocol, m.MsgType, payload, m.BodyOffset, m.BodyLength, fromClient));
            }
        }
    }

    /// <summary>Sends a frame to the server as though the client had produced it.</summary>
    public async Task InjectAsync(BgoWriter w, CancellationToken ct = default)
    {
        var ss = _toServer;
        if (ss is null) throw new InvalidOperationException("No client session to inject into.");

        var frame = w.ToFrame();
        var payload = frame.AsSpan(2).ToArray();

        await _serverWriteLock.WaitAsync(ct);
        try { await ss.WriteFrameAsync(payload, ct); }
        finally { _serverWriteLock.Release(); }

        FramesInjected++;
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
