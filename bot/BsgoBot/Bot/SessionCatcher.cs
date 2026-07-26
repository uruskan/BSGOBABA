using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace BsgoBot.Bot;

/// <summary>Everything the launcher passed its own client on the command line.</summary>
public readonly record struct CapturedSession(
    string Host,
    string PlayerId,
    string Session,
    string Version,
    string Language,
    int ProcessId);

/// <summary>
/// Watches for the real launcher's <c>bsgo.exe</c> and lifts the session off its command line.
///
/// The launcher hands its client a one-shot session token as <c>+session</c>, along with the
/// server, the player id and the client version. The bot needs all four to relaunch the client
/// through the proxy, and none of them are on the wire — the launcher's client talks straight to
/// the live server, so the proxy never sees that connection at all. The command line is the only
/// place they exist.
///
/// A client already pointed at 127.0.0.1 is ignored: that is our own relaunch, not the launcher.
///
/// Ported from <c>capture-session.ps1</c>, which did the same thing through WMI. The command line
/// is read straight out of the process instead, so this needs no extra package and no PowerShell.
/// </summary>
public sealed class SessionCatcher : IDisposable
{
    private CancellationTokenSource? _cts;
    private readonly HashSet<int> _seen = [];

    public bool Running => _cts is not null;

    /// <summary>
    /// Kill the launcher's own client the moment we read its session.
    ///
    /// On by default because that is the whole point: the token is single-use, so the proxied
    /// relaunch has to be the first thing that spends it. Leave it off only if you want to watch
    /// what the launcher's client does.
    /// </summary>
    public bool KillLauncherClient { get; set; } = true;

    public int PollMilliseconds { get; set; } = 200;

    public event Action<string>? Log;
    public event Action<CapturedSession>? Captured;

    public void Start()
    {
        if (Running) return;
        _seen.Clear();
        _cts = new CancellationTokenSource();
        _ = WatchAsync(_cts.Token);
        Log?.Invoke("Session catcher armed — log in through the bsgo.fun launcher now.");
    }

    public void Stop()
    {
        if (!Running) return;
        _cts!.Cancel();
        _cts.Dispose();
        _cts = null;
        Log?.Invoke("Session catcher stopped.");
    }

    public void Dispose() => Stop();

    private async Task WatchAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Sweep();
                await Task.Delay(PollMilliseconds, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log?.Invoke("Session catcher stopped: " + ex.Message);
        }
    }

    private void Sweep()
    {
        var live = new HashSet<int>();

        foreach (var p in Process.GetProcessesByName("bsgo"))
        {
            using (p)
            {
                live.Add(p.Id);
                if (!_seen.Add(p.Id)) continue;      // already dealt with this one

                string? cmd = TryReadCommandLine(p.Id);
                if (cmd is null) continue;

                var host = Flag(cmd, "+gameServer");
                if (host is null) continue;

                // Our own relaunch goes through the proxy, which is local. Capturing that would
                // overwrite a real session with a pointer to ourselves.
                if (host is "127.0.0.1" or "localhost") continue;

                var session = Flag(cmd, "+session");
                if (session is null)
                {
                    Log?.Invoke($"Found a client pointed at {host} with no +session — ignoring it.");
                    continue;
                }

                var captured = new CapturedSession(
                    host,
                    Flag(cmd, "+userID") ?? "",
                    session,
                    Flag(cmd, "+version") ?? "",
                    Flag(cmd, "+language") ?? "en",
                    p.Id);

                Log?.Invoke($"Captured a session from the launcher's client (pid {p.Id}): "
                          + $"{host}, player {captured.PlayerId}, version {captured.Version}.");

                if (KillLauncherClient) Kill(p);

                Captured?.Invoke(captured);
            }
        }

        // Forget dead pids, so the next login is captured even when Windows reuses the number.
        _seen.RemoveWhere(id => !live.Contains(id));
    }

    private void Kill(Process p)
    {
        try
        {
            p.Kill();
            Log?.Invoke("Killed the launcher's client so the session is still unspent.");
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Could not close the launcher's client (pid {p.Id}): {ex.Message}. "
                      + "The session may already be spent — close it by hand and try again.");
        }
    }

    /// <summary>
    /// Value of one <c>+flag</c>. The whitespace is required: without it <c>+session</c> also
    /// matches <c>+sessionID</c>, which is a different, useless value.
    /// </summary>
    private static string? Flag(string commandLine, string flag)
    {
        var m = Regex.Match(commandLine, Regex.Escape(flag) + @"\s+(\S+)");
        return m.Success ? m.Groups[1].Value : null;
    }

    // ---------------------------------------------------------------- reading the command line
    //
    // A process's command line lives in its own address space, in the RTL_USER_PROCESS_PARAMETERS
    // block hanging off the PEB. Windows exposes no API for reading another process's, so the
    // walk is: find the PEB, read the parameter block pointer out of it, read the UNICODE_STRING
    // at the command-line offset, then read the string itself.
    //
    // The wrinkle is bitness. This bot is 64-bit; the game client is 32-bit (it installs under
    // "Program Files (x86)"). A WOW64 process has a second, 32-bit PEB with its own layout, and
    // ProcessWow64Information is what hands us its address. Both paths are implemented, because
    // "it happens to be 32-bit today" is not a thing to bake in.

    private const int ProcessBasicInformation = 0;
    private const int ProcessWow64Information = 26;

    private const int ProcessQueryInformation = 0x0400;
    private const int ProcessQueryLimitedInformation = 0x1000;
    private const int ProcessVmRead = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInfo
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr process, int infoClass, ref ProcessBasicInfo info, int length, out int written);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr process, int infoClass, out IntPtr info, int length, out int written);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(
        IntPtr process, IntPtr address, byte[] buffer, int size, out IntPtr read);

    private string? TryReadCommandLine(int processId)
    {
        IntPtr handle = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, processId);
        if (handle == IntPtr.Zero)
            handle = OpenProcess(ProcessQueryLimitedInformation | ProcessVmRead, false, processId);
        if (handle == IntPtr.Zero) return null;

        try
        {
            // A 32-bit target under WOW64 has its own PEB, and its structures are half the width.
            if (NtQueryInformationProcess(handle, ProcessWow64Information, out IntPtr peb32, IntPtr.Size, out _) == 0
                && peb32 != IntPtr.Zero)
                return ReadWow64(handle, peb32);

            var pbi = new ProcessBasicInfo();
            if (NtQueryInformationProcess(handle, ProcessBasicInformation, ref pbi,
                    Marshal.SizeOf<ProcessBasicInfo>(), out _) != 0) return null;
            if (pbi.PebBaseAddress == IntPtr.Zero) return null;

            return ReadNative(handle, pbi.PebBaseAddress);
        }
        catch
        {
            // A client that exits mid-read is normal — we kill them ourselves.
            return null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>64-bit: PEB.ProcessParameters at 0x20, CommandLine at 0x70 as a UNICODE_STRING
    /// (ushort Length, ushort MaximumLength, uint padding, ulong Buffer).</summary>
    private string? ReadNative(IntPtr process, IntPtr peb)
    {
        if (Read(process, peb + 0x20, 8) is not { } p || BitConverter.ToUInt64(p) is 0) return null;
        ulong parameters = BitConverter.ToUInt64(p);

        if (Read(process, (IntPtr)(parameters + 0x70), 16) is not { } u) return null;
        int length = BitConverter.ToUInt16(u, 0);
        ulong buffer = BitConverter.ToUInt64(u, 8);
        return ReadString(process, (IntPtr)buffer, length);
    }

    /// <summary>32-bit under WOW64: PEB32.ProcessParameters at 0x10, CommandLine at 0x40 as a
    /// UNICODE_STRING32 (ushort Length, ushort MaximumLength, uint Buffer).</summary>
    private string? ReadWow64(IntPtr process, IntPtr peb32)
    {
        if (Read(process, peb32 + 0x10, 4) is not { } p) return null;
        uint parameters = BitConverter.ToUInt32(p);
        if (parameters == 0) return null;

        if (Read(process, (IntPtr)(parameters + 0x40), 8) is not { } u) return null;
        int length = BitConverter.ToUInt16(u, 0);
        uint buffer = BitConverter.ToUInt32(u, 4);
        return ReadString(process, (IntPtr)buffer, length);
    }

    private string? ReadString(IntPtr process, IntPtr at, int lengthInBytes)
    {
        // Length is in bytes and excludes the terminator. Anything wilder than a few KB means we
        // read a pointer that wasn't one, so refuse it rather than allocate on a bad number.
        if (at == IntPtr.Zero || lengthInBytes is <= 0 or > 32 * 1024) return null;
        return Read(process, at, lengthInBytes) is { } raw
            ? System.Text.Encoding.Unicode.GetString(raw)
            : null;
    }

    private static byte[]? Read(IntPtr process, IntPtr address, int size)
    {
        var buffer = new byte[size];
        return ReadProcessMemory(process, address, buffer, size, out var read) && (int)read == size
            ? buffer
            : null;
    }
}
