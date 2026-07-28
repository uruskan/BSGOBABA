using BsgoBot.Bot;

namespace BsgoBot.Ui;

/// <summary>
/// The diagnostics, laid out as titled cards flowing into columns, with every value wrapped
/// rather than clipped. Replaces the one-column monospace dump whose every line ended in "…" —
/// a diagnostic that cannot be read answers nothing.
/// </summary>
public sealed class DiagView : Control
{
    private readonly VScrollBar _scroll = new();
    private List<DiagSection> _sections = [];

    // Layout cache, rebuilt when the data or the width changes. Measuring wrapped text for
    // every row four times a second would be paint-time work for identical output.
    private readonly List<CardLayout> _cards = [];
    private int _contentHeight;
    private int _layoutWidth = -1;

    private const int Gap = 8;          // between cards and around the edge
    private const int Pad = 10;         // inside a card
    private const int HeaderHeight = 24;
    private const int LabelWidth = 96;
    private const int RowGap = 3;
    private const int MinColWidth = 360;

    private sealed record RowLayout(DiagRow Row, int Y, int Height);
    private sealed record CardLayout(string Title, int X, int Y, int W, int H, List<RowLayout> Rows);

    public DiagView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Bg;

        _scroll.Dock = DockStyle.Right;
        _scroll.Width = 12;
        _scroll.Scroll += (_, _) => Invalidate();
        Controls.Add(_scroll);
    }

    public void SetSections(List<DiagSection> sections)
    {
        // Rebuild only on real change; this is fed from a 250 ms timer.
        if (_sections.Count == sections.Count
            && _sections.Zip(sections).All(p =>
                   p.First.Title == p.Second.Title && p.First.Rows.SequenceEqual(p.Second.Rows)))
            return;

        _sections = sections;
        _layoutWidth = -1;
        Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        _layoutWidth = -1;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        ScrollTo(_scroll.Value - Math.Sign(e.Delta) * 60);
    }

    private void ScrollTo(int value)
    {
        int max = Math.Max(0, _contentHeight - Height);
        _scroll.Value = Math.Clamp(value, 0, Math.Max(0, max));
        Invalidate();
    }

    /// <summary>Masonry: each card lands in the currently shortest column, so a long section
    /// (the catalogue) doesn't leave a page of blank space beside it.</summary>
    private void Relayout()
    {
        int width = Width - _scroll.Width;
        _cards.Clear();
        _layoutWidth = width;

        int cols = Math.Max(1, (width - Gap) / (MinColWidth + Gap));
        int colWidth = (width - Gap * (cols + 1)) / cols;
        var colHeights = new int[cols];
        for (int i = 0; i < cols; i++) colHeights[i] = Gap;

        foreach (var s in _sections)
        {
            if (s.Rows.Count == 0) continue;

            var rows = new List<RowLayout>();
            int y = HeaderHeight;
            foreach (var row in s.Rows)
            {
                int valueWidth = row.Label.Length > 0
                    ? colWidth - Pad * 2 - LabelWidth
                    : colWidth - Pad * 2;
                int h = Math.Max(15, TextRenderer.MeasureText(row.Value, Theme.MonoSmall,
                    new Size(Math.Max(40, valueWidth), int.MaxValue),
                    TextFormatFlags.WordBreak).Height);
                rows.Add(new RowLayout(row, y, h));
                y += h + RowGap;
            }
            int cardHeight = y - RowGap + Pad;

            int col = 0;
            for (int i = 1; i < cols; i++) if (colHeights[i] < colHeights[col]) col = i;

            _cards.Add(new CardLayout(s.Title, Gap + col * (colWidth + Gap), colHeights[col],
                                      colWidth, cardHeight, rows));
            colHeights[col] += cardHeight + Gap;
        }

        _contentHeight = colHeights.Max();

        int max = Math.Max(0, _contentHeight - Height);
        _scroll.Maximum = Math.Max(0, _contentHeight);
        _scroll.LargeChange = Math.Max(1, Height);
        _scroll.SmallChange = 40;
        _scroll.Value = Math.Min(_scroll.Value, Math.Max(0, max));
        _scroll.Visible = max > 0;
    }

    private static Color ToneColor(DiagTone tone) => tone switch
    {
        DiagTone.Muted => Theme.Muted,
        DiagTone.Good => Theme.Good,
        DiagTone.Warn => Theme.Warn,
        DiagTone.Bad => Theme.Bad,
        DiagTone.Accent => Theme.Accent,
        _ => Theme.Text,
    };

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Bg);

        if (_layoutWidth != Width - _scroll.Width) Relayout();

        if (_cards.Count == 0)
        {
            TextRenderer.DrawText(g, "Nothing to report yet — connect the game through the proxy.",
                Theme.Ui, new Rectangle(0, 0, Width, Height), Theme.Faint,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }

        int top = _scroll.Value;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        foreach (var card in _cards)
        {
            int y0 = card.Y - top;
            if (y0 + card.H < 0 || y0 > Height) continue;   // off screen

            var r = new RectangleF(card.X + 0.5f, y0 + 0.5f, card.W - 1f, card.H - 1f);
            Theme.FillRounded(g, r, 8f, Theme.Card);
            Theme.DrawRounded(g, r, 8f, Theme.Border);

            using (var accent = new SolidBrush(Theme.Accent))
                g.FillRectangle(accent, card.X + Pad, y0 + 8, 2, 9);
            using (var head = new SolidBrush(Theme.Muted))
                Theme.DrawTracked(g, card.Title.ToUpperInvariant(), Theme.Header, head,
                                  card.X + Pad + 8, y0 + 6.5f);

            foreach (var row in card.Rows)
            {
                int ry = y0 + row.Y;
                if (ry + row.Height < 0 || ry > Height) continue;

                if (row.Row.Label.Length > 0)
                {
                    TextRenderer.DrawText(g, row.Row.Label, Theme.MonoSmall,
                        new Rectangle(card.X + Pad, ry, LabelWidth - 4, row.Height), Theme.Faint,
                        TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.EndEllipsis);
                    TextRenderer.DrawText(g, row.Row.Value, Theme.MonoSmall,
                        new Rectangle(card.X + Pad + LabelWidth, ry,
                                      card.W - Pad * 2 - LabelWidth, row.Height),
                        ToneColor(row.Row.Tone),
                        TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak);
                }
                else
                {
                    TextRenderer.DrawText(g, row.Row.Value, Theme.MonoSmall,
                        new Rectangle(card.X + Pad, ry, card.W - Pad * 2, row.Height),
                        row.Row.Tone == DiagTone.Normal ? Theme.Muted : ToneColor(row.Row.Tone),
                        TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak);
                }
            }
        }
    }
}
