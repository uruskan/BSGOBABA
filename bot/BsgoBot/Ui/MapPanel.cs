using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Numerics;
using BsgoBot.Bot;
using BsgoBot.Protocol;
using BsgoBot.World;

namespace BsgoBot.Ui;

/// <summary>
/// Orbiting tactical projection of the sector.
///
/// The server streams every object in the sector; your game client then hides most of them
/// locally, in DradisHelper, against your ship's detection radii. This panel draws all of
/// them and bands each contact by how your client is treating it — so the outermost band,
/// DARK, is exactly the intel the game is refusing to show you.
///
/// Projection is an orthographic tilt: yaw spins the sector, tilt lifts the plane towards
/// the horizon, and height above the plane is drawn as a stalk with a shadow. Orthographic
/// rather than perspective on purpose — distances stay comparable across the whole view,
/// which is the entire point of a tactical map.
/// </summary>
public sealed class MapPanel : Control
{
    private readonly WorldState _world;
    private readonly FarmBot _bot;

    private float _yaw;
    private float _tilt = 0.95f;                 // ~55 degrees off vertical
    private Point _dragFrom;
    private bool _dragging;

    /// <summary>Whether the mouse actually moved between press and release. A drag that ends
    /// where it began is a click on a contact; without this test, every orbit also reselected
    /// whatever happened to be under the cursor when the button came up.</summary>
    private bool _moved;

    private readonly HashSet<ContactLayer> _hidden = [];
    private readonly List<(Rectangle Box, ContactLayer Layer)> _legendHits = [];

    /// <summary>Where each contact ended up on screen in the last paint, so a click can be
    /// turned back into an object. Rebuilt every frame — the projection changes with the view,
    /// so there is nothing to cache.</summary>
    private readonly List<(PointF At, uint Id, float Size)> _contactHits = [];

    private uint _hover;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public float Range { get; set; } = 5000f;    // world units from centre to screen edge

    /// <summary>The contact the contacts list and the map agree is selected.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public uint Selected { get; set; }

    /// <summary>Raised when the wheel changes the zoom, so the toolbar slider can follow.</summary>
    public event Action<float>? RangeChanged;

    /// <summary>Raised when you click a contact. Single click selects, so the panels stay in step.</summary>
    public event Action<uint>? ContactPicked;

    /// <summary>Raised when you double-click a contact — "that one, now".</summary>
    public event Action<uint>? ContactActivated;

    public MapPanel(WorldState world, FarmBot bot)
    {
        _world = world;
        _bot = bot;
        DoubleBuffered = true;
        SetStyle(ControlStyles.Selectable, true);
        BackColor = Color.FromArgb(8, 11, 16);
        TabStop = false;
    }

    // ---------------------------------------------------------------- palette

    private static Color ColorFor(SpaceEntityType t) => t switch
    {
        SpaceEntityType.BotFighter or SpaceEntityType.AsteroidBot => Color.FromArgb(255, 96, 64),
        SpaceEntityType.Cruiser or SpaceEntityType.MiningShip => Color.FromArgb(255, 150, 110),
        SpaceEntityType.Player => Color.FromArgb(90, 190, 255),
        SpaceEntityType.Asteroid or SpaceEntityType.Planetoid => Color.FromArgb(215, 175, 90),
        SpaceEntityType.CargoObject or SpaceEntityType.Debris => Color.FromArgb(110, 240, 170),
        SpaceEntityType.Outpost or SpaceEntityType.WeaponPlatform => Color.FromArgb(210, 130, 255),
        SpaceEntityType.Missile or SpaceEntityType.Mine or SpaceEntityType.SmartMine => Color.FromArgb(255, 70, 70),
        SpaceEntityType.JumpBeacon or SpaceEntityType.JumpTargetTransponder => Color.FromArgb(90, 235, 235),
        SpaceEntityType.Planet => Color.FromArgb(120, 130, 160),
        _ => Color.FromArgb(120, 132, 148),
    };

    private static int SizeFor(SpaceEntityType t) => t switch
    {
        SpaceEntityType.Outpost or SpaceEntityType.Planet => 11,
        SpaceEntityType.Cruiser or SpaceEntityType.WeaponPlatform => 9,
        SpaceEntityType.BotFighter or SpaceEntityType.AsteroidBot or SpaceEntityType.Player
            or SpaceEntityType.MiningShip => 7,
        SpaceEntityType.Asteroid or SpaceEntityType.Planetoid => 6,
        SpaceEntityType.Missile => 4,
        _ => 5,
    };

    /// <summary>How a band is rendered. Opacity carries "how hidden is this from you".</summary>
    private static (int Alpha, bool Filled, bool Dashed) StyleFor(ContactLayer l) => l switch
    {
        ContactLayer.Visual => (255, true, false),
        ContactLayer.Dradis => (225, true, false),
        ContactLayer.Map => (160, true, false),
        ContactLayer.Dark => (200, false, true),
        _ => (200, true, false),
    };

    private static Color LayerTint(ContactLayer l) => l switch
    {
        ContactLayer.Visual => Color.FromArgb(150, 235, 255),
        ContactLayer.Dradis => Color.FromArgb(90, 200, 230),
        ContactLayer.Map => Color.FromArgb(80, 140, 170),
        ContactLayer.Dark => Color.FromArgb(255, 190, 80),
        _ => Color.FromArgb(140, 140, 150),
    };

    // ---------------------------------------------------------------- projection

    private PointF Project(Vector3 rel, PointF centre, float scale)
    {
        float cy = MathF.Cos(_yaw), sy = MathF.Sin(_yaw);
        float x = rel.X * cy - rel.Z * sy;
        float z = rel.X * sy + rel.Z * cy;
        float ct = MathF.Cos(_tilt), st = MathF.Sin(_tilt);
        return new PointF(centre.X + x * scale, centre.Y - (z * ct + rel.Y * st) * scale);
    }

    /// <summary>Distance into the screen, for painter's-algorithm ordering.</summary>
    private float Depth(Vector3 rel)
    {
        float z = rel.X * MathF.Sin(_yaw) + rel.Z * MathF.Cos(_yaw);
        return z * MathF.Sin(_tilt) - rel.Y * MathF.Cos(_tilt);
    }

    // ---------------------------------------------------------------- paint

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);

        int w = Width, h = Height;
        if (w < 40 || h < 40) return;

        var centre = new PointF(w / 2f, h * 0.56f);
        float scale = Math.Min(w, h) / 2.2f / Range;
        var now = DateTime.UtcNow;
        _contactHits.Clear();

        using var font = new Font("Consolas", 7.5f);
        using var fontBold = new Font("Consolas", 8f, FontStyle.Bold);

        DrawPlaneGlow(g, centre, scale);

        if (!_world.MyPositionKnown)
        {
            // Centred, and clear of the HUD line along the top edge.
            using var warn = new SolidBrush(Color.FromArgb(200, 150, 160));
            using var quiet = new SolidBrush(Color.FromArgb(96, 110, 128));
            const string a = "AWAITING POSITION FIX";
            const string b = "Launch the client and jump into a sector.";
            var sa = g.MeasureString(a, fontBold);
            var sb = g.MeasureString(b, font);
            g.DrawString(a, fontBold, warn, centre.X - sa.Width / 2f, centre.Y - 14);
            g.DrawString(b, font, quiet, centre.X - sb.Width / 2f, centre.Y + 4);
            DrawHud(g, font, w, h, 0, DetectionRanges.None, null);
            return;
        }

        var me = _world.MyPosition;
        var detection = _world.Detection;
        var objects = _world.Snapshot();

        DrawGrid(g, centre, scale);
        DrawRings(g, centre, scale, font, detection);

        // Fades the plane out towards its rim so the grid reads as receding distance instead
        // of wallpaper. Over the plane but under the contacts, because the far contacts are
        // the whole point of this panel and must stay crisp.
        DrawHaze(g, centre, scale);

        // Painter's algorithm: far contacts first so near ones overlap them correctly.
        var contacts = new List<(SpaceObj Obj, Vector3 Rel, ContactLayer Layer, float Dist, float Depth)>();
        var counts = new Dictionary<ContactLayer, int>();

        foreach (var o in objects)
        {
            if (o.IsMe || !o.HasPosition) continue;
            var rel = o.PredictedPosition(now) - me;
            float dist = rel.Length();
            var layer = Visibility.Classify(o, dist, detection);
            counts[layer] = counts.GetValueOrDefault(layer) + 1;
            if (_hidden.Contains(layer)) continue;
            contacts.Add((o, rel, layer, dist, Depth(rel)));
        }

        contacts.Sort((a, b) => b.Depth.CompareTo(a.Depth));

        uint targetId = _bot.CurrentTarget;
        var brushes = new Dictionary<int, SolidBrush>();
        SolidBrush Brush(Color c)
        {
            if (!brushes.TryGetValue(c.ToArgb(), out var b)) brushes[c.ToArgb()] = b = new SolidBrush(c);
            return b;
        }

        try
        {
            // Label only the handful of DARK contacts that matter — that band is the point of
            // this panel, but labelling forty of them would bury it.
            var labelled = contacts
                .Where(c => c.Layer == ContactLayer.Dark)
                .OrderBy(c => c.Dist)
                .Take(5)
                .Select(c => c.Obj.Id)
                .ToHashSet();

            foreach (var (o, rel, layer, dist, _) in contacts)
            {
                var ground = new Vector3(rel.X, 0f, rel.Z);
                var pTop = Project(rel, centre, scale);
                var pGround = Project(ground, centre, scale);

                if (pTop.X < -40 || pTop.Y < -40 || pTop.X > w + 40 || pTop.Y > h + 40) continue;

                var baseColour = ColorFor(o.Type);
                var (alpha, filled, dashed) = StyleFor(layer);
                if (o.Cloaked) alpha = (int)(alpha * 0.55f);
                int size = SizeFor(o.Type);

                // Recorded in draw order, i.e. far to near, so the hit test walking backwards
                // finds the nearest contact first — the one you can actually see.
                _contactHits.Add((pTop, o.Id, size));

                // Stalk + shadow: the only honest way to show height on a flat surface.
                if (Math.Abs(rel.Y) > 1f)
                {
                    using var stalk = new Pen(Color.FromArgb(alpha / 3, baseColour), 1f);
                    if (dashed) stalk.DashStyle = DashStyle.Dot;
                    g.DrawLine(stalk, pGround, pTop);
                    g.FillEllipse(Brush(Color.FromArgb(alpha / 4, baseColour)),
                        pGround.X - 1.5f, pGround.Y - 1.5f, 3f, 3f);
                }

                if (layer is ContactLayer.Visual or ContactLayer.Dradis)
                {
                    using var glow = new GraphicsPath();
                    glow.AddEllipse(pTop.X - size, pTop.Y - size, size * 2, size * 2);
                    using var halo = new PathGradientBrush(glow)
                    {
                        CenterColor = Color.FromArgb(alpha / 3, baseColour),
                        SurroundColors = [Color.FromArgb(0, baseColour)],
                    };
                    g.FillPath(halo, glow);
                }

                if (filled)
                {
                    g.FillEllipse(Brush(Color.FromArgb(alpha, baseColour)),
                        pTop.X - size / 2f, pTop.Y - size / 2f, size, size);
                }
                else
                {
                    // Hollow ring: a contact the game is not drawing for you at all.
                    using var ring = new Pen(Color.FromArgb(alpha, baseColour), 1.4f);
                    g.DrawEllipse(ring, pTop.X - size / 2f, pTop.Y - size / 2f, size, size);
                }

                // Selection is drawn under the lock ring and in a different shape, because they
                // mean different things: what you are looking at, versus what the bot is shooting.
                if (o.Id == Selected)
                {
                    using var ring = new Pen(Theme.Accent, 1.4f) { DashStyle = DashStyle.Dash };
                    g.DrawEllipse(ring, pTop.X - 15, pTop.Y - 15, 30, 30);
                }

                if (o.Id == targetId)
                {
                    using var lock1 = new Pen(Color.White, 1.6f);
                    g.DrawEllipse(lock1, pTop.X - 11, pTop.Y - 11, 22, 22);
                    using var lead = new Pen(Color.FromArgb(110, Color.White)) { DashStyle = DashStyle.Dash };
                    g.DrawLine(lead, centre, pTop);

                    string label = $"{o.Type}  {dist:F0}u  {Visibility.Describe(layer)}"
                                 // Points, not a ratio — we never learn other objects' maximums.
                                 + (o.StatsKnown ? $"  hull {o.Hull:F0}" : "");
                    g.DrawString(label, fontBold, Brush(Color.White), pTop.X + 14, pTop.Y - 7);
                }
                else if (o.Id == Selected || o.Id == _hover)
                {
                    string name = _world.NameOf(o) ?? o.Type.ToString();
                    g.DrawString($"{name}  {dist:F0}u  {Visibility.Describe(layer)}", fontBold,
                        Brush(o.Id == Selected ? Theme.Accent : Color.White), pTop.X + 14, pTop.Y - 7);
                }
                else if (labelled.Contains(o.Id))
                {
                    g.DrawString($"{o.Type} {dist:F0}u", font,
                        Brush(Color.FromArgb(200, LayerTint(ContactLayer.Dark))), pTop.X + 9, pTop.Y - 6);
                }
            }

            DrawOwnShip(g, centre, scale);
            DrawHud(g, font, w, h, objects.Count, detection, counts);
        }
        finally
        {
            foreach (var b in brushes.Values) b.Dispose();
        }
    }

    /// <summary>
    /// A circle on the tilted plane projects to an ellipse squashed by cos(tilt). Matching the
    /// clip and the fade to that shape is what makes the grid read as a surface receding away
    /// rather than as wallpaper behind the contacts.
    /// </summary>
    private RectangleF PlaneDisc(PointF centre, float scale, float worldRadius)
    {
        float rx = worldRadius * scale;
        float ry = rx * MathF.Cos(_tilt);
        return new RectangleF(centre.X - rx, centre.Y - ry, rx * 2, ry * 2);
    }

    /// <summary>
    /// A pool of light lying on the plane. Confined to the plane ellipse on purpose — a glow
    /// that spilled past the rim would leave the grid's clipped edge showing as a hard line.
    /// </summary>
    private void DrawPlaneGlow(Graphics g, PointF centre, float scale)
    {
        var disc = PlaneDisc(centre, scale, Range * 1.5f);
        using var path = new GraphicsPath();
        path.AddEllipse(disc);
        using var glow = new PathGradientBrush(path)
        {
            CenterColor = Color.FromArgb(26, 40, 56),
            SurroundColors = [Color.FromArgb(8, 11, 16)],
            CenterPoint = centre,
        };
        g.FillEllipse(glow, disc);
    }

    /// <summary>Fades the plane out at its rim, so it ends in darkness instead of at the frame.</summary>
    private void DrawHaze(Graphics g, PointF centre, float scale)
    {
        var disc = PlaneDisc(centre, scale, Range * 1.5f);
        using var path = new GraphicsPath();
        path.AddEllipse(disc);
        using var haze = new PathGradientBrush(path)
        {
            CenterColor = Color.FromArgb(0, 8, 11, 16),
            SurroundColors = [Color.FromArgb(255, 8, 11, 16)],
            CenterPoint = centre,
            FocusScales = new PointF(0.55f, 0.55f),
        };
        g.FillEllipse(haze, disc);
    }

    /// <summary>The plane your ship sits on. Rotating with yaw is what sells the third axis.</summary>
    private void DrawGrid(Graphics g, PointF centre, float scale)
    {
        float step = NiceStep(Range / 3f);
        int lines = (int)Math.Ceiling(Range * 1.6f / step);

        // Confine the grid to the plane disc: a square grid filling the window would give the
        // tilt away as a fake immediately.
        var saved = g.Save();
        using (var clip = new GraphicsPath())
        {
            // Same radius as the haze, so every line is fully faded by the time it is cut.
            clip.AddEllipse(PlaneDisc(centre, scale, Range * 1.5f));
            g.SetClip(clip, CombineMode.Replace);

            using var faint = new Pen(Color.FromArgb(40, 100, 138));
            using var minor = new Pen(Color.FromArgb(18, 80, 112));
            using var axis = new Pen(Color.FromArgb(85, 125, 175, 210));

            for (int i = -lines; i <= lines; i++)
            {
                float d = i * step;
                float ext = lines * step;
                // Every fourth line stays a touch brighter, which gives the eye a scale to
                // read without needing more lines.
                var pen = i == 0 ? axis : (i % 4 == 0 ? faint : minor);
                g.DrawLine(pen,
                    Project(new Vector3(d, 0, -ext), centre, scale),
                    Project(new Vector3(d, 0, ext), centre, scale));
                g.DrawLine(pen,
                    Project(new Vector3(-ext, 0, d), centre, scale),
                    Project(new Vector3(ext, 0, d), centre, scale));
            }
        }
        g.Restore(saved);
    }

    /// <summary>
    /// Weapon reach plus the two radii your client filters the sector with, drawn on the
    /// plane. The readout goes in the corner rather than on the rings: at a steep tilt the
    /// rings squash together and on-ring labels pile up over the target.
    /// </summary>
    private void DrawRings(Graphics g, PointF centre, float scale, Font font, DetectionRanges det)
    {
        (float Radius, Color Colour, string Label)[] rings =
        [
            (det.Map, Color.FromArgb(70, 110, 140), "MAP"),
            (det.Dradis, Color.FromArgb(70, 175, 205), "DRADIS"),
            (det.Visual, Color.FromArgb(120, 220, 245), "VISUAL"),
            (WeaponReach(), Color.FromArgb(120, 235, 150), "REACH"),
        ];

        foreach (var (radius, colour, _) in rings)
        {
            if (radius <= 0f || radius * scale > Math.Max(Width, Height) * 2.5f) continue;

            using var pen = new Pen(colour) { DashStyle = DashStyle.Dash };
            const int segments = 72;
            var pts = new PointF[segments + 1];
            for (int i = 0; i <= segments; i++)
            {
                float a = i * MathF.Tau / segments;
                pts[i] = Project(new Vector3(MathF.Cos(a) * radius, 0, MathF.Sin(a) * radius), centre, scale);
            }
            g.DrawLines(pen, pts);
        }

        float y = 26f;
        foreach (var (radius, colour, label) in rings)
        {
            if (radius <= 0f) continue;
            using var swatch = new Pen(colour, 1.4f) { DashStyle = DashStyle.Dash };
            g.DrawLine(swatch, Width - 130, y + 6, Width - 118, y + 6);
            using var text = new SolidBrush(Color.FromArgb(200, colour));
            g.DrawString($"{label,-6} {radius,6:F0}u", font, text, Width - 112, y);
            y += 14f;
        }
    }

    private void DrawOwnShip(Graphics g, PointF centre, float scale)
    {
        using var hull = new Pen(Color.White, 2f);
        g.DrawLine(hull, centre.X - 7, centre.Y, centre.X + 7, centre.Y);
        g.DrawLine(hull, centre.X, centre.Y - 7, centre.X, centre.Y + 7);

        var v = _world.MyVelocity;
        if (v.LengthSquared() > 1f)
        {
            var lead = Vector3.Normalize(v) * (Range * 0.12f);
            using var vec = new Pen(Color.FromArgb(200, 255, 255, 255), 1.6f);
            g.DrawLine(vec, centre, Project(lead, centre, scale));
        }

        // North marker, so a rotated view is still readable.
        using var north = new SolidBrush(Color.FromArgb(120, 150, 200, 230));
        using var f = new Font("Consolas", 7f, FontStyle.Bold);
        var n = Project(new Vector3(0, 0, Range * 0.92f), centre, scale);
        g.DrawString("N", f, north, n.X - 4, n.Y - 6);
    }

    /// <summary>Counters plus the clickable band legend.</summary>
    private void DrawHud(Graphics g, Font font, int w, int h, int total,
        DetectionRanges det, Dictionary<ContactLayer, int>? counts)
    {
        using var dim = new SolidBrush(Color.FromArgb(110, 128, 145));
        g.DrawString(
            $"view {Range:F0}u   yaw {_yaw * 180f / MathF.PI:F0}°   tilt {90f - _tilt * 180f / MathF.PI:F0}°   "
            + "drag to orbit · wheel to zoom · click a contact to select · double-click it to pin",
            font, dim, 8, 6);

        _legendHits.Clear();
        if (counts is null) return;

        ContactLayer[] bands = [ContactLayer.Visual, ContactLayer.Dradis, ContactLayer.Map, ContactLayer.Dark];
        if (counts.ContainsKey(ContactLayer.Unknown)) bands = [ContactLayer.Unknown];

        int y = h - 18 - bands.Length * 15;
        g.DrawString("CONTACTS", font, dim, 10, y - 16);

        foreach (var band in bands)
        {
            int n = counts.GetValueOrDefault(band);
            bool on = !_hidden.Contains(band);
            var tint = LayerTint(band);

            var box = new Rectangle(10, y, 150, 14);
            _legendHits.Add((box, band));

            using var swatch = new SolidBrush(Color.FromArgb(on ? 230 : 60, tint));
            if (band == ContactLayer.Dark)
            {
                using var ring = new Pen(Color.FromArgb(on ? 230 : 60, tint), 1.4f);
                g.DrawEllipse(ring, 11, y + 3, 8, 8);
            }
            else
            {
                g.FillEllipse(swatch, 11, y + 3, 8, 8);
            }

            using var text = new SolidBrush(Color.FromArgb(on ? 220 : 90, tint));
            string note = band switch
            {
                ContactLayer.Dark => "your client hides these",
                ContactLayer.Unknown => "no detection radii published",
                _ => "",
            };
            g.DrawString($"{Visibility.Describe(band),-8}{n,4}  {note}", font, text, 24, y);
            y += 15;
        }

        using var footer = new SolidBrush(Color.FromArgb(90, 105, 122));
        string ranges = det.Known
            ? $"dradis {det.Dradis:F0}u · map {det.Map:F0}u"
            : "detection radii not sent by this server";
        g.DrawString($"{total} objects tracked · {ranges}", font, footer, 10, h - 16);
    }

    private float WeaponReach()
    {
        var role = _bot.Mode == FarmMode.Mining ? WeaponRole.Mining : WeaponRole.Combat;
        var guns = _bot.Weapons.For(role);
        if (guns.Count == 0) return _bot.FallbackRange;
        var known = guns.Where(x => x.MaxRange is > 0).Select(x => x.MaxRange!.Value).ToList();
        return known.Count > 0 ? known.Max() : _bot.FallbackRange;
    }

    /// <summary>Rounds a grid step to 1/2/5 x 10^n so the spacing is always a readable number.</summary>
    private static float NiceStep(float raw)
    {
        if (raw <= 0) return 1000f;
        float mag = MathF.Pow(10, MathF.Floor(MathF.Log10(raw)));
        float norm = raw / mag;
        float snapped = norm < 1.5f ? 1f : norm < 3.5f ? 2f : norm < 7.5f ? 5f : 10f;
        return snapped * mag;
    }

    // ---------------------------------------------------------------- interaction

    /// <summary>
    /// The contact drawn under a point, or 0.
    ///
    /// Walked backwards because the paint runs far to near: the last thing drawn at a spot is
    /// the one in front, and that is the one you meant to click. The tolerance is generous —
    /// a five-pixel dot is not something anyone can hit exactly.
    /// </summary>
    private uint ContactAt(Point p)
    {
        for (int i = _contactHits.Count - 1; i >= 0; i--)
        {
            var (at, id, size) = _contactHits[i];
            float r = Math.Max(9f, size);
            float dx = p.X - at.X, dy = p.Y - at.Y;
            if (dx * dx + dy * dy <= r * r) return id;
        }
        return 0;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();

        foreach (var (box, layer) in _legendHits)
        {
            if (!box.Contains(e.Location)) continue;
            if (!_hidden.Remove(layer)) _hidden.Add(layer);
            Invalidate();
            return;
        }

        if (e.Button == MouseButtons.Left) { _dragging = true; _moved = false; _dragFrom = e.Location; }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (!_dragging)
        {
            uint under = ContactAt(e.Location);
            Cursor = under == 0 ? Cursors.Default : Cursors.Hand;
            if (under != _hover) { _hover = under; Invalidate(); }
            return;
        }

        // A couple of pixels of slop: a click always shifts the mouse a little, and treating
        // that as an orbit would make contacts almost impossible to select.
        if (Math.Abs(e.X - _dragFrom.X) > 2 || Math.Abs(e.Y - _dragFrom.Y) > 2) _moved = true;

        _yaw -= (e.X - _dragFrom.X) * 0.008f;
        _tilt = Math.Clamp(_tilt - (e.Y - _dragFrom.Y) * 0.006f, 0.05f, 1.48f);
        _dragFrom = e.Location;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        bool wasDrag = _moved;
        _dragging = false;
        _moved = false;
        if (wasDrag || e.Button != MouseButtons.Left) return;

        uint id = ContactAt(e.Location);
        if (id == 0) return;
        Selected = id;
        ContactPicked?.Invoke(id);
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        Range = Math.Clamp(Range * (e.Delta > 0 ? 0.85f : 1.18f), 300f, 60000f);
        RangeChanged?.Invoke(Range);
        Invalidate();
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);

        // Double-clicking a contact means "that one"; double-clicking the void still resets the
        // view, which is what the HUD line has always promised.
        uint id = ContactAt(e.Location);
        if (id != 0)
        {
            Selected = id;
            ContactActivated?.Invoke(id);
            Invalidate();
            return;
        }

        _yaw = 0f;
        _tilt = 0.95f;
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        if (FindForm()?.ContainsFocus == true) Focus();   // so the wheel reaches us
    }
}
