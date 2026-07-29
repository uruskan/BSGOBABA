using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BsgoBot.Bot;

/// <summary>
/// Keeps the game client from logging itself out for inactivity.
///
/// The 30-minute overnight "crashes" were never the server and never a crash: the client's own
/// <c>InputDispatcher</c> counts PHYSICAL keyboard and mouse input, and after
/// <c>LOGOUT_DELAY = 1800</c> seconds without any it calls <c>DoLogoutEx</c> and quits to the
/// launcher in good order. Nothing on the wire resets that clock — the bot can fly the ship all
/// night and still look, to the client, like an empty chair.
///
/// So this presses a key. Any keydown reaching the client resets the clock
/// (<c>InputReceiverKeyboard.NotifyAboutKeyDown</c> fires before the bindings are consulted),
/// with two constraints taken from the decompiled source: a repeat of the SAME code is ignored
/// (<c>inputCode != lastInput</c>), hence two keys used in alternation; and the key should do
/// nothing else, hence F14/F15 — keys real keyboards don't have and the client never binds.
///
/// Delivered with PostMessage to the client's window, which does not need focus. Whether an
/// unfocused Unity 5 player accepts posted keys varies by build — if the kept client-exit logs
/// ever show a ~30-minute session dying AFTER these nudges started appearing in the log, this
/// approach is refuted and the fallback is focus-steal + SendInput.
/// </summary>
public sealed class AntiIdle
{
    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_KEYUP = 0x0101;
    private const int VK_F14 = 0x7D;
    private const int VK_F15 = 0x7E;

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    public event Action<string>? Log;

    /// <summary>Well inside the client's 30-minute fuse, wasteful of nothing.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(10);

    private DateTime _lastNudge = DateTime.MinValue;
    private bool _useF15;
    private bool _missingSaid;

    /// <summary>
    /// Call from any slow, regular tick with whether the farm wants the client kept alive.
    /// Sends at most one key per <see cref="Interval"/>; quietly nothing in between.
    /// </summary>
    public void Tick(bool active)
    {
        if (!active) return;
        var now = DateTime.UtcNow;
        if (now - _lastNudge < Interval) return;

        var procs = Process.GetProcessesByName("bsgo");
        try
        {
            var hwnd = procs.Select(p => p.MainWindowHandle).FirstOrDefault(h => h != IntPtr.Zero);
            if (hwnd == IntPtr.Zero)
            {
                if (!_missingSaid)
                {
                    _missingSaid = true;
                    Log?.Invoke("Anti-idle found no bsgo window to keep awake — if the client is "
                              + "running under another exe name, its 30-minute logout is live.");
                }
                return;
            }
            _missingSaid = false;
            _lastNudge = now;

            // Alternate the key because the client ignores a repeat of the last input code.
            int vk = _useF15 ? VK_F15 : VK_F14;
            _useF15 = !_useF15;

            PostMessage(hwnd, WM_KEYDOWN, (IntPtr)vk, (IntPtr)0x00000001);
            PostMessage(hwnd, WM_KEYUP, (IntPtr)vk, unchecked((IntPtr)0xC0000001));
            Log?.Invoke($"Anti-idle: pressed F{(vk == VK_F15 ? 15 : 14)} into the client — its "
                      + "30-minute inactivity logout is reset.");
        }
        finally
        {
            foreach (var p in procs) p.Dispose();
        }
    }
}
