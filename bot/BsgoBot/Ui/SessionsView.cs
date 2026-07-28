using BsgoBot.Bot;
using BsgoBot.Protocol;

namespace BsgoBot.Ui;

/// <summary>
/// The farm run history: when the bot ran, in what, where, and what it banked — one row per
/// run, newest first. This is the table that settles which loadout, ship and sector actually
/// mine best, because the ore/hour column already contains all the travel and downtime.
/// </summary>
public sealed class SessionsView : Control
{
    private readonly VScrollBar _scroll = new();
    private List<FarmSession> _sessions = [];

    private const int SummaryHeight = 66;
    private const int HeaderHeight = 24;
    private const int RowHeight = 22;
    private const int Pad = 10;

    private sealed record Col(string Title, int Width, bool Right);

    private static readonly Col[] Cols =
    [
        new("started", 108, false),
        new("ran for", 64, true),
        new("sector", 58, true),
        new("ship", 120, false),
        new("ore", 80, true),
        new("ore/h", 74, true),
        new("tylium", 80, true),
        new("water", 80, true),
        new("titanium", 80, true),
        new("cubits", 70, true),
        new("other", 70, true),
        new("deaths", 52, true),
    ];

    public SessionsView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Bg;

        _scroll.Dock = DockStyle.Right;
        _scroll.Width = 12;
        _scroll.Scroll += (_, _) => Invalidate();
        Controls.Add(_scroll);
    }

    /// <summary>Newest first, the live run (if any) at the top.</summary>
    public void SetSessions(List<FarmSession> sessions)
    {
        _sessions = sessions;
        Invalidate();
    }

    public static string FmtSpan(TimeSpan t) =>
        t.TotalHours >= 24 ? $"{(int)t.TotalDays}d {t.Hours}h"
        : t.TotalMinutes >= 60 ? $"{(int)t.TotalHours}h {t.Minutes:D2}m"
        : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m {t.Seconds:D2}s"
        : $"{t.Seconds}s";

    private static string FmtCount(long n) =>
        n >= 1_000_000 ? $"{n / 1_000_000.0:F1}M" : n >= 10_000 ? $"{n / 1000.0:F1}k" : $"{n:N0}";

    /// <summary>Cubit sums are fractional — a unit of ore is worth 0.05–0.2 — so small values
    /// keep one decimal where the big ones borrow the count format.</summary>
    private static string FmtCubits(double c) =>
        c >= 10_000 ? FmtCount((long)c) : c >= 100 ? c.ToString("F0") : c.ToString("F1");

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        int max = Math.Max(0, _sessions.Count - VisibleRows());
        _scroll.Value = Math.Clamp(_scroll.Value - Math.Sign(e.Delta) * 3, 0, Math.Max(0, max));
        Invalidate();
    }

    private int VisibleRows() =>
        Math.Max(1, (Height - SummaryHeight - HeaderHeight) / RowHeight);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Bg);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        var now = DateTime.UtcNow;
        var live = _sessions.FirstOrDefault(s => s.Running);
        var finished = _sessions.Where(s => !s.Running).ToList();

        DrawSummary(g, live, finished, now);

        // ---- table
        int tableTop = SummaryHeight;
        int x = Pad;
        foreach (var c in Cols)
        {
            var rect = new Rectangle(x, tableTop, c.Width, HeaderHeight);
            TextRenderer.DrawText(g, c.Title, Theme.Header, rect, Theme.Faint,
                (c.Right ? TextFormatFlags.Right : TextFormatFlags.Left) | TextFormatFlags.Bottom);
            x += c.Width;
        }
        using (var pen = new Pen(Theme.Border))
            g.DrawLine(pen, Pad, tableTop + HeaderHeight + 2, x, tableTop + HeaderHeight + 2);

        if (_sessions.Count == 0)
        {
            TextRenderer.DrawText(g, "No farm runs recorded yet — press Go farm and history "
                + "will accumulate here.", Theme.Ui,
                new Rectangle(Pad, tableTop + HeaderHeight + 10, Width - Pad * 2, 40),
                Theme.Faint, TextFormatFlags.Left | TextFormatFlags.Top);
            return;
        }

        // Best finished ore/hour gets its cell lit green: that is the row to copy.
        double best = 0;
        foreach (var s in finished)
            if (s.OrePerHour(now) is { } oph && oph > best) best = oph;

        int visible = VisibleRows();
        int maxTop = Math.Max(0, _sessions.Count - visible);
        _scroll.Maximum = Math.Max(0, _sessions.Count - 1);
        _scroll.LargeChange = Math.Max(1, visible);
        _scroll.Value = Math.Min(_scroll.Value, maxTop);
        _scroll.Visible = maxTop > 0;

        int y = tableTop + HeaderHeight + 5;
        foreach (var s in _sessions.Skip(_scroll.Value).Take(visible))
        {
            DrawRow(g, s, y, best, now);
            y += RowHeight;
        }
    }

    private void DrawSummary(Graphics g, FarmSession? live, List<FarmSession> finished, DateTime now)
    {
        var r = new RectangleF(Pad + 0.5f, 6.5f, Width - _scroll.Width - Pad * 2 - 1f, SummaryHeight - 13f);
        Theme.FillRounded(g, r, 8f, Theme.Card);
        Theme.DrawRounded(g, r, 8f, live is not null ? Theme.AccentDeep : Theme.Border);

        string headline;
        string detail;
        Color tone;
        if (live is not null)
        {
            headline = $"RUNNING for {FmtSpan(live.Duration(now))} — started "
                     + $"{live.StartedUtc.ToLocalTime():HH:mm}";
            string ore = live.OrePerHour(now) is { } oph ? $"{FmtCount((long)oph)} ore/h" : "measuring…";
            detail = $"{FmtCount(live.Mined)} ore ≈ {FmtCubits(live.CubitValue)} cubits ({ore})"
                   + (live.Ship.Length > 0 ? $" — {live.Ship}" : "")
                   + (live.SectorId != 0 ? $" — sector {live.SectorId}" : "");
            tone = Theme.Good;
        }
        else if (finished.Count > 0)
        {
            var last = finished[0];
            headline = $"Stopped — last run {last.StartedUtc.ToLocalTime():MMM d HH:mm}, "
                     + $"ran {FmtSpan(last.Duration(now))}";
            string ore = last.OrePerHour(now) is { } oph ? $" ({FmtCount((long)oph)} ore/h)" : "";
            detail = $"{FmtCount(last.Mined)} ore ≈ {FmtCubits(last.CubitValue)} cubits{ore} "
                   + $"over {finished.Count} recorded run(s)";
            tone = Theme.Muted;
        }
        else
        {
            headline = "No runs yet";
            detail = "Press Go farm — start/stop times and everything banked land here.";
            tone = Theme.Faint;
        }

        TextRenderer.DrawText(g, headline, Theme.UiBold,
            new Rectangle(Pad + 12, 13, Width - Pad * 4, 20), tone,
            TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(g, detail, Theme.Ui,
            new Rectangle(Pad + 12, 33, Width - Pad * 4, 18), Theme.Text,
            TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.EndEllipsis);
    }

    private void DrawRow(Graphics g, FarmSession s, int y, double bestOph, DateTime now)
    {
        long ty = s.Gained.GetValueOrDefault((uint)ResourceType.Tylium);
        long wa = s.Gained.GetValueOrDefault((uint)ResourceType.Water);
        long ti = s.Gained.GetValueOrDefault((uint)ResourceType.Titanium);
        long other = s.TotalGained - ty - wa - ti;
        double? oph = s.OrePerHour(now);

        var cells = new (string Text, Color Color)[]
        {
            (s.StartedUtc.ToLocalTime().ToString("MMM d HH:mm"), Theme.Muted),
            (s.Running ? "live" : FmtSpan(s.Duration(now)), s.Running ? Theme.Good : Theme.Text),
            (s.SectorId == 0 ? "?" : s.SectorId.ToString(), s.SectorId == 0 ? Theme.Faint : Theme.Text),
            (s.Ship.Length == 0 ? "?" : s.Ship, s.Ship.Length == 0 ? Theme.Faint : Theme.Text),
            (FmtCount(s.Mined), s.Mined > 0 ? Theme.Text : Theme.Faint),
            (oph is null ? "…" : FmtCount((long)oph.Value),
                !s.Running && oph is { } o1 && bestOph > 0 && Math.Abs(o1 - bestOph) < 0.5
                    ? Theme.Good : Theme.Accent),
            (FmtCount(ty), ty > 0 ? Theme.Text : Theme.Faint),
            (FmtCount(wa), wa > 0 ? Theme.Text : Theme.Faint),
            (FmtCount(ti), ti > 0 ? Theme.Text : Theme.Faint),
            (FmtCubits(s.CubitValue), s.CubitValue > 0 ? Theme.Accent : Theme.Faint),
            (FmtCount(other), other > 0 ? Theme.Warn : Theme.Faint),
            (s.Deaths.ToString(), s.Deaths > 0 ? Theme.Bad : Theme.Faint),
        };

        if (s.Running)
        {
            using var hi = new SolidBrush(Color.FromArgb(26, Theme.Good));
            g.FillRectangle(hi, Pad, y, Cols.Sum(c => c.Width), RowHeight);
        }

        int x = Pad;
        for (int i = 0; i < Cols.Length; i++)
        {
            var rect = new Rectangle(x + 2, y, Cols[i].Width - 8, RowHeight);
            TextRenderer.DrawText(g, cells[i].Text, Theme.MonoSmall, rect, cells[i].Color,
                (Cols[i].Right ? TextFormatFlags.Right : TextFormatFlags.Left)
                | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            x += Cols[i].Width;
        }
    }
}
