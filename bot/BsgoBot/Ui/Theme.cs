using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace BsgoBot.Ui;

/// <summary>
/// One place for every colour, font and shape in the app. WinForms ships with a 2008 look
/// by default; nothing here is decoration for its own sake — the palette is the same one the
/// tactical map draws with, so a green number on the panel means the same thing as a green
/// ring on the map.
/// </summary>
public static class Theme
{
    public static readonly Color Bg = Color.FromArgb(10, 13, 18);
    public static readonly Color Panel = Color.FromArgb(15, 19, 26);
    public static readonly Color Card = Color.FromArgb(20, 25, 33);
    public static readonly Color CardHi = Color.FromArgb(27, 34, 44);
    public static readonly Color Border = Color.FromArgb(36, 45, 58);
    public static readonly Color Text = Color.FromArgb(224, 232, 242);
    public static readonly Color Muted = Color.FromArgb(122, 137, 156);
    public static readonly Color Faint = Color.FromArgb(78, 90, 106);

    public static readonly Color Accent = Color.FromArgb(64, 196, 230);
    public static readonly Color AccentDeep = Color.FromArgb(22, 78, 96);
    public static readonly Color Good = Color.FromArgb(96, 220, 150);
    public static readonly Color Warn = Color.FromArgb(240, 184, 84);
    public static readonly Color Bad = Color.FromArgb(238, 102, 98);

    public static readonly Font Ui = Pick(9f, FontStyle.Regular, "Segoe UI Variable Text", "Segoe UI");
    public static readonly Font UiBold = Pick(9f, FontStyle.Bold, "Segoe UI Variable Text", "Segoe UI");
    public static readonly Font UiSmall = Pick(8f, FontStyle.Regular, "Segoe UI Variable Text", "Segoe UI");

    /// <summary>Section headers: small, bold, wide-tracked, upper case.</summary>
    public static readonly Font Header = Pick(7.5f, FontStyle.Bold, "Segoe UI Variable Text", "Segoe UI");

    public static readonly Font Mono = Pick(8.75f, FontStyle.Regular, "Cascadia Mono", "Consolas");
    public static readonly Font MonoBold = Pick(8.75f, FontStyle.Bold, "Cascadia Mono", "Consolas");
    public static readonly Font MonoSmall = Pick(8f, FontStyle.Regular, "Cascadia Mono", "Consolas");

    private static Font Pick(float size, FontStyle style, params string[] families)
    {
        foreach (var name in families)
        {
            try
            {
                using var probe = new FontFamily(name);
                return new Font(probe, size, style);
            }
            catch (ArgumentException)
            {
                // Not installed on this machine — try the next one.
            }
        }
        return new Font(FontFamily.GenericSansSerif, size, style);
    }

    /// <summary>Letter-spaced caption, the cheap trick that makes a header look designed.</summary>
    public static void DrawTracked(Graphics g, string text, Font font, Brush brush, float x, float y,
        float tracking = 1.4f)
    {
        foreach (char c in text)
        {
            var s = c.ToString();
            g.DrawString(s, font, brush, x, y);
            x += g.MeasureString(s, font, PointF.Empty, StringFormat.GenericTypographic).Width + tracking;
        }
    }

    public static GraphicsPath Rounded(RectangleF r, float radius)
    {
        float d = Math.Min(radius * 2f, Math.Min(r.Width, r.Height));
        var p = new GraphicsPath();
        if (d <= 0.5f) { p.AddRectangle(r); return p; }

        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static void FillRounded(Graphics g, RectangleF r, float radius, Color fill)
    {
        using var path = Rounded(r, radius);
        using var b = new SolidBrush(fill);
        g.FillPath(b, path);
    }

    public static void DrawRounded(Graphics g, RectangleF r, float radius, Color stroke, float width = 1f)
    {
        using var path = Rounded(r, radius);
        using var p = new Pen(stroke, width);
        g.DrawPath(p, path);
    }

    // ---------------------------------------------------------------- window chrome

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    // 20 on Windows 10 2004 and later; the pre-release builds used 19. Setting both is
    // harmless — the wrong one returns a failure code we ignore.
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeLegacy = 19;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    /// <summary>
    /// Paints the title bar dark. Without this the window keeps a white caption bar above a
    /// black app, which is the single most dated thing about a default WinForms window.
    /// Silently does nothing on Windows builds that predate the attributes.
    /// </summary>
    public static void UseDarkTitleBar(Form form)
    {
        if (!form.IsHandleCreated) return;

        try
        {
            int on = 1;
            DwmSetWindowAttribute(form.Handle, DwmwaUseImmersiveDarkMode, ref on, sizeof(int));
            DwmSetWindowAttribute(form.Handle, DwmwaUseImmersiveDarkModeLegacy, ref on, sizeof(int));

            // Windows 11 lets the caption be coloured outright. COLORREF is 0x00BBGGRR.
            int caption = Bgr(Panel);
            int border = Bgr(Border);
            int text = Bgr(Text);
            DwmSetWindowAttribute(form.Handle, DwmwaCaptionColor, ref caption, sizeof(int));
            DwmSetWindowAttribute(form.Handle, DwmwaBorderColor, ref border, sizeof(int));
            DwmSetWindowAttribute(form.Handle, DwmwaTextColor, ref text, sizeof(int));
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    private static int Bgr(Color c) => c.R | (c.G << 8) | (c.B << 16);
}
