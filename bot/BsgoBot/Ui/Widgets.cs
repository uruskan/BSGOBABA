using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Drawing.Drawing2D;

namespace BsgoBot.Ui;

/// <summary>Rounded, flat, hover-aware button. Primary ones carry the accent fill.</summary>
public sealed class FlatButton : Button
{
    private bool _hover;
    private bool _down;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Primary { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color Tint { get; set; } = Color.Empty;

    public FlatButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        BackColor = Theme.Card;
        ForeColor = Theme.Text;
        Font = Theme.Ui;
        Cursor = Cursors.Hand;
        Height = 30;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Panel);

        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        var accent = Tint == Color.Empty ? Theme.Accent : Tint;

        Color fill, border, text;
        if (!Enabled)
        {
            fill = Theme.Card; border = Theme.Border; text = Theme.Faint;
        }
        else if (Primary)
        {
            fill = _down ? Blend(accent, Color.Black, 0.35f) : _hover ? accent : Blend(accent, Theme.Card, 0.22f);
            border = accent;
            text = _hover || _down ? Color.FromArgb(8, 14, 18) : accent;
        }
        else
        {
            fill = _down ? Theme.Card : _hover ? Theme.CardHi : Theme.Card;
            border = _hover ? Blend(accent, Theme.Border, 0.5f) : Theme.Border;
            text = _hover ? Theme.Text : Theme.Muted;
        }

        Theme.FillRounded(g, r, 6f, fill);
        Theme.DrawRounded(g, r, 6f, border);

        TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, Width, Height), text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    internal static Color Blend(Color a, Color b, float t) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
}

/// <summary>A pill that toggles. Replaces the checkbox, which cannot be themed at all.</summary>
public sealed class ToggleChip : Control
{
    private bool _hover;
    private bool _checked;

    public event EventHandler? CheckedChanged;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Checked
    {
        get => _checked;
        set { if (_checked == value) return; _checked = value; Invalidate(); CheckedChanged?.Invoke(this, EventArgs.Empty); }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color Tint { get; set; } = Theme.Accent;

    /// <summary>Arbitrary payload, so a chip can stand for an enum value.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public object? Tag2 { get; set; }

    public ToggleChip(string text, bool isChecked = false)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Text = text;
        _checked = isChecked;
        Font = Theme.UiSmall;
        Cursor = Cursors.Hand;
        Height = 26;
        Width = MeasureWidth();
    }

    private int MeasureWidth()
    {
        using var g = CreateGraphics();
        return TextRenderer.MeasureText(g, Text, Font).Width + 34;
    }

    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Width = MeasureWidth(); Invalidate(); }
    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnClick(EventArgs e) { Checked = !Checked; base.OnClick(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Panel);

        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        var fill = _checked ? FlatButton.Blend(Tint, Theme.Card, 0.78f)
                            : _hover ? Theme.CardHi : Theme.Card;
        var border = _checked ? Tint : _hover ? FlatButton.Blend(Tint, Theme.Border, 0.55f) : Theme.Border;

        Theme.FillRounded(g, r, Height / 2f, fill);
        Theme.DrawRounded(g, r, Height / 2f, border);

        // A filled dot reads as "on" faster than a tick at this size.
        float d = 7f, cx = 12f, cy = Height / 2f;
        using (var dot = new SolidBrush(_checked ? Tint : Theme.Faint))
            g.FillEllipse(dot, cx - d / 2f, cy - d / 2f, d, d);
        if (_checked)
        {
            using var halo = new Pen(Color.FromArgb(70, Tint), 3f);
            g.DrawEllipse(halo, cx - d / 2f - 2f, cy - d / 2f - 2f, d + 4f, d + 4f);
        }

        TextRenderer.DrawText(g, Text, Font,
            new Rectangle(20, 0, Width - 26, Height),
            _checked ? Theme.Text : Theme.Muted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}

/// <summary>Dark, flat drop-down. The stock ComboBox paints a white list however it is coloured.</summary>
public sealed class DarkCombo : ComboBox
{
    private bool _hover;

    public DarkCombo()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer, true);
        DropDownStyle = ComboBoxStyle.DropDownList;
        DrawMode = DrawMode.OwnerDrawFixed;
        FlatStyle = FlatStyle.Flat;
        BackColor = Theme.Card;
        ForeColor = Theme.Text;
        Font = Theme.UiSmall;
        ItemHeight = 20;
        Height = 26;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        bool selected = (e.State & DrawItemState.Selected) != 0;
        using (var bg = new SolidBrush(selected ? Theme.AccentDeep : Theme.Card))
            e.Graphics.FillRectangle(bg, e.Bounds);
        TextRenderer.DrawText(e.Graphics, Items[e.Index]?.ToString() ?? "", Font,
            new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 8, e.Bounds.Height),
            selected ? Theme.Text : Theme.Muted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Panel);

        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        Theme.FillRounded(g, r, 5f, _hover ? Theme.CardHi : Theme.Card);
        Theme.DrawRounded(g, r, 5f, _hover ? FlatButton.Blend(Theme.Accent, Theme.Border, 0.55f) : Theme.Border);

        TextRenderer.DrawText(g, SelectedItem?.ToString() ?? Text, Font,
            new Rectangle(7, 0, Width - 26, Height), Theme.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        // Chevron, drawn rather than left to the OS.
        using var pen = new Pen(Theme.Muted, 1.5f);
        float cx = Width - 14, cy = Height / 2f - 1;
        g.DrawLines(pen, [new PointF(cx - 4, cy - 1.5f), new PointF(cx, cy + 2.5f), new PointF(cx + 4, cy - 1.5f)]);
    }
}

/// <summary>Numeric field with a real text box and drawn steppers, so typing still works.</summary>
public sealed class NumberField : Control
{
    private readonly TextBox _box = new();
    private int _min, _max, _step;
    private bool _hoverUp, _hoverDown;

    public event EventHandler? ValueChanged;

    public NumberField(int min, int max, int step, int value, string? suffix = null)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        _min = min; _max = max; _step = step;
        Suffix = suffix;
        Height = 26;
        Width = 92;
        BackColor = Theme.Card;

        _box.BorderStyle = BorderStyle.None;
        _box.BackColor = Theme.Card;
        _box.ForeColor = Theme.Text;
        _box.Font = Theme.Mono;
        _box.TextAlign = HorizontalAlignment.Right;
        _box.Text = value.ToString();
        _box.KeyPress += (_, e) =>
        {
            if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar)) e.Handled = true;
        };
        _box.LostFocus += (_, _) => Commit();
        _box.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { Commit(); e.SuppressKeyPress = true; } };
        Controls.Add(_box);

        _value = Math.Clamp(value, min, max);
    }

    public string? Suffix { get; }

    private int _value;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Value
    {
        get => _value;
        set
        {
            int v = Math.Clamp(value, _min, _max);
            if (v == _value) return;
            _value = v;
            _box.Text = v.ToString();
            Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Commit()
    {
        Value = int.TryParse(_box.Text, out var v) ? v : _value;
        _box.Text = _value.ToString();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        int suffixWidth = Suffix is null ? 0 : TextRenderer.MeasureText(Suffix, Theme.MonoSmall).Width + 2;
        _box.SetBounds(8, (Height - _box.PreferredHeight) / 2 + 1, Width - 26 - suffixWidth, _box.PreferredHeight);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        bool up = e.X > Width - 18 && e.Y < Height / 2;
        bool down = e.X > Width - 18 && e.Y >= Height / 2;
        if (up != _hoverUp || down != _hoverDown) { _hoverUp = up; _hoverDown = down; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverUp = _hoverDown = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.X <= Width - 18) { _box.Focus(); return; }
        Value += e.Y < Height / 2 ? _step : -_step;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        Value += e.Delta > 0 ? _step : -_step;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Panel);

        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        Theme.FillRounded(g, r, 5f, Theme.Card);
        Theme.DrawRounded(g, r, 5f, _box.Focused ? Theme.Accent : Theme.Border);

        if (Suffix is not null)
        {
            int sw = TextRenderer.MeasureText(Suffix, Theme.MonoSmall).Width;
            TextRenderer.DrawText(g, Suffix, Theme.MonoSmall,
                new Rectangle(Width - 20 - sw, 0, sw, Height), Theme.Faint,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        }

        using var up = new Pen(_hoverUp ? Theme.Accent : Theme.Faint, 1.4f);
        using var dn = new Pen(_hoverDown ? Theme.Accent : Theme.Faint, 1.4f);
        float cx = Width - 11;
        g.DrawLines(up, [new PointF(cx - 3.5f, 10), new PointF(cx, 6.5f), new PointF(cx + 3.5f, 10)]);
        g.DrawLines(dn, [new PointF(cx - 3.5f, Height - 10), new PointF(cx, Height - 6.5f), new PointF(cx + 3.5f, Height - 10)]);
    }
}

/// <summary>
/// Themed single-line text entry. The stock TextBox cannot be given a border colour, so the
/// real box is borderless and inset into a control that draws the frame around it.
///
/// Blank is a meaningful value here: the slot editor uses it for "I don't know this number",
/// which is different from zero and is why <see cref="NumberField"/> won't do.
/// </summary>
public sealed class TextField : Control
{
    private readonly TextBox _box = new();

    public event EventHandler? Committed;

    public TextField(string text = "", string? placeholder = null, bool numeric = false)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = 26;
        Width = 140;
        BackColor = Theme.Card;
        Placeholder = placeholder;

        _box.BorderStyle = BorderStyle.None;
        _box.BackColor = Theme.Card;
        _box.ForeColor = Theme.Text;
        _box.Font = numeric ? Theme.Mono : Theme.Ui;
        _box.Text = text;
        if (numeric)
            _box.KeyPress += (_, e) =>
            {
                // A decimal point and a minus are legal; anything else that isn't a digit is not.
                if (char.IsControl(e.KeyChar) || char.IsDigit(e.KeyChar)) return;
                if (e.KeyChar is '.' or ',' && !_box.Text.Contains('.') && !_box.Text.Contains(',')) return;
                e.Handled = true;
            };
        _box.GotFocus += (_, _) => Invalidate();
        _box.LostFocus += (_, _) => { Invalidate(); Committed?.Invoke(this, EventArgs.Empty); };
        // Forwarded, because the real text lives on the inner box: without this a caller that
        // subscribes to TextChanged on the field would never hear anything.
        _box.TextChanged += (_, _) => { Invalidate(); OnTextChanged(EventArgs.Empty); };
        Controls.Add(_box);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string? Placeholder { get; set; }

    [AllowNull]
    public override string Text
    {
        get => _box.Text;
        set { _box.Text = value ?? ""; Invalidate(); }
    }

    /// <summary>The text as a number, or null when it is blank or unparsable — which is exactly
    /// the distinction the slot editor needs between "50 power" and "I didn't say".</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public float? Number
    {
        get
        {
            var t = _box.Text.Trim().Replace(',', '.');
            return float.TryParse(t, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
        }
        set => _box.Text = value is null ? "" : value.Value.ToString("0.###",
            System.Globalization.CultureInfo.InvariantCulture);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        _box.SetBounds(8, (Height - _box.PreferredHeight) / 2 + 1, Width - 16, _box.PreferredHeight);
    }

    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); _box.Focus(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Panel);

        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        Theme.FillRounded(g, r, 5f, Theme.Card);
        Theme.DrawRounded(g, r, 5f, _box.Focused ? Theme.Accent : Theme.Border);

        if (Placeholder is not null && _box.Text.Length == 0 && !_box.Focused)
            TextRenderer.DrawText(g, Placeholder, Theme.UiSmall,
                new Rectangle(9, 0, Width - 16, Height), Theme.Faint,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}

/// <summary>A titled card. Everything on the right-hand rail sits in one of these.</summary>
public class Card : Panel
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Title { get; set; } = "";
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string? Note { get; set; }

    public Card(string title)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Title = title;
        BackColor = Theme.Panel;
        Padding = new Padding(12, 30, 12, 10);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Theme.Panel);

        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        Theme.FillRounded(g, r, 8f, Theme.Card);
        Theme.DrawRounded(g, r, 8f, Theme.Border);

        using var accent = new SolidBrush(Theme.Accent);
        g.FillRectangle(accent, 12, 12, 2, 9);

        using var head = new SolidBrush(Theme.Muted);
        Theme.DrawTracked(g, Title.ToUpperInvariant(), Theme.Header, head, 20, 10.5f);

        if (Note is not null)
        {
            using var note = new SolidBrush(Theme.Faint);
            var size = g.MeasureString(Note, Theme.UiSmall);
            g.DrawString(Note, Theme.UiSmall, note, Width - 12 - size.Width, 9);
        }
    }
}

/// <summary>Label/value rows. Sizes itself to its content, so nothing gets clipped.</summary>
public sealed class StatList : Control
{
    public sealed record Row(string Label, string Value, Color? ValueColor = null, bool Spacer = false);

    private List<Row> _rows = [];

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int RowHeight { get; set; } = 16;

    public StatList()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Card;
    }

    public void SetRows(List<Row> rows)
    {
        // Only repaint when something actually changed; this runs on a 250 ms timer.
        if (_rows.Count == rows.Count && _rows.SequenceEqual(rows)) return;
        _rows = rows;
        Height = PreferredHeight;
        Invalidate();
    }

    [Browsable(false)]
    public int PreferredHeight => _rows.Sum(r => r.Spacer ? RowHeight / 2 : RowHeight) + 4;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Card);

        float y = 2;
        foreach (var row in _rows)
        {
            if (row.Spacer) { y += RowHeight / 2f; continue; }

            TextRenderer.DrawText(g, row.Label, Theme.MonoSmall,
                new Rectangle(0, (int)y, Width / 2, RowHeight), Theme.Faint,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

            TextRenderer.DrawText(g, row.Value, Theme.Mono,
                new Rectangle(Width / 2 - 10, (int)y, Width / 2 + 10, RowHeight),
                row.ValueColor ?? Theme.Text,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            y += RowHeight;
        }
    }
}

/// <summary>Scrolling event log with severity colouring and no 1990s border.</summary>
public sealed class LogView : Control
{
    private readonly List<(DateTime At, string Text, Color Colour)> _lines = [];
    private readonly Lock _gate = new();
    private readonly VScrollBar _scroll = new();
    private int _top;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int LineHeight { get; set; } = 15;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Capacity { get; set; } = 500;

    public LogView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Bg;

        _scroll.Dock = DockStyle.Right;
        _scroll.Width = 10;
        _scroll.Scroll += (_, _) => { _top = _scroll.Value; Invalidate(); };
        Controls.Add(_scroll);
    }

    public void Add(string text)
    {
        var colour = Classify(text);
        lock (_gate)
        {
            _lines.Add((DateTime.Now, text, colour));
            while (_lines.Count > Capacity) _lines.RemoveAt(0);
        }
        ScrollToEnd();
        Invalidate();
    }

    /// <summary>Colour by what the line means, so a failure is findable at a glance.</summary>
    private static Color Classify(string t)
    {
        if (t.Contains("WARNING", StringComparison.OrdinalIgnoreCase) ||
            t.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
            t.Contains("rejected", StringComparison.OrdinalIgnoreCase) ||
            t.Contains("could not", StringComparison.OrdinalIgnoreCase)) return Theme.Bad;

        if (t.Contains("Learned", StringComparison.OrdinalIgnoreCase) ||
            t.Contains("destroyed", StringComparison.OrdinalIgnoreCase) ||
            t.Contains("Identified", StringComparison.OrdinalIgnoreCase) ||
            t.Contains("connected", StringComparison.OrdinalIgnoreCase)) return Theme.Good;

        if (t.Contains("Engaging", StringComparison.OrdinalIgnoreCase) ||
            t.Contains("Mining", StringComparison.OrdinalIgnoreCase) ||
            t.Contains("Taking", StringComparison.OrdinalIgnoreCase)) return Theme.Accent;

        return Theme.Muted;
    }

    private int VisibleLines => Math.Max(1, (Height - 8) / LineHeight);

    private void ScrollToEnd()
    {
        lock (_gate)
        {
            int max = Math.Max(0, _lines.Count - VisibleLines);
            _top = max;
            if (IsHandleCreated) BeginInvoke(SyncScroll);
        }
    }

    private void SyncScroll()
    {
        int count;
        lock (_gate) count = _lines.Count;
        int max = Math.Max(0, count - VisibleLines);
        _scroll.Maximum = Math.Max(0, count - 1);
        _scroll.LargeChange = Math.Max(1, VisibleLines);
        _scroll.Value = Math.Clamp(_top, 0, Math.Max(0, _scroll.Maximum));
        _scroll.Visible = max > 0;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        int count;
        lock (_gate) count = _lines.Count;
        _top = Math.Clamp(_top - Math.Sign(e.Delta) * 3, 0, Math.Max(0, count - VisibleLines));
        SyncScroll();
        Invalidate();
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); SyncScroll(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Bg);

        using var divider = new Pen(Theme.Border);
        g.DrawLine(divider, 0, 0, Width, 0);

        (DateTime At, string Text, Color Colour)[] snapshot;
        lock (_gate) snapshot = _lines.Skip(_top).Take(VisibleLines).ToArray();

        int y = 4;
        foreach (var (at, text, colour) in snapshot)
        {
            TextRenderer.DrawText(g, at.ToString("HH:mm:ss"), Theme.MonoSmall,
                new Rectangle(10, y, 60, LineHeight), Theme.Faint,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

            TextRenderer.DrawText(g, text, Theme.MonoSmall,
                new Rectangle(74, y, Width - 90, LineHeight), colour,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            y += LineHeight;
        }
    }
}

/// <summary>Monospace read-only text block for the diagnostics dump.</summary>
public sealed class MonoText : Control
{
    private string[] _lines = [];
    private int _top;

    public MonoText()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Card;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int LineHeight { get; set; } = 15;

    public void SetText(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (_lines.SequenceEqual(lines)) return;
        _lines = lines;
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        int visible = Math.Max(1, Height / LineHeight);
        _top = Math.Clamp(_top - Math.Sign(e.Delta) * 3, 0, Math.Max(0, _lines.Length - visible));
        Invalidate();
    }

    /// <summary>Start of the first run of two or more spaces that isn't the line's own indent.</summary>
    private static int IndexOfGap(string line)
    {
        int from = 0;
        while (from < line.Length && line[from] == ' ') from++;
        int i = line.IndexOf("  ", from, StringComparison.Ordinal);
        return i;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Card);

        int visible = Math.Max(1, Height / LineHeight);
        int y = 0;
        foreach (var line in _lines.Skip(_top).Take(visible))
        {
            // Lines are written as "label<gap>value". Split on the gap rather than a fixed
            // column so indented continuation lines (the weapon list) aren't chopped mid-word.
            int gap = IndexOfGap(line);
            if (gap < 0)
            {
                TextRenderer.DrawText(g, line, Theme.MonoSmall, new Rectangle(0, y, Width, LineHeight),
                    line.StartsWith("  ", StringComparison.Ordinal) ? Theme.Muted : Theme.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
            else
            {
                TextRenderer.DrawText(g, line[..gap], Theme.MonoSmall,
                    new Rectangle(0, y, 108, LineHeight), Theme.Faint,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                TextRenderer.DrawText(g, line[gap..].TrimStart(), Theme.MonoSmall,
                    new Rectangle(108, y, Width - 108, LineHeight), Theme.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
            y += LineHeight;
        }
    }
}
