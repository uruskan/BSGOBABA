using System.Drawing.Drawing2D;
using BsgoBot.Bot;
using BsgoBot.Protocol;
using BsgoBot.World;

namespace BsgoBot.Ui;

/// <summary>
/// Your ship's hardware, laid out the way the game lays it out: four hexes above the hull for
/// the weapon slots, and the ability bar along the bottom.
///
/// The bot can work out a great deal about a slot from the wire — its damage, its reach, its
/// reload — but there are two things it fundamentally cannot. It cannot tell which slot is
/// which hex in the game's own UI, because nothing on the wire carries that mapping. And it
/// cannot tell a damage-control module from an armour plate from the resource scanner, because
/// all three publish no damage and no weapon range and look identical. This panel is where you
/// settle both, once, by reading the card in game and typing it in.
///
/// Nothing here is required. Everything the server does publish still arrives and still wins on
/// numbers — declaring a slot fixes what it is *for*, not what it does.
/// </summary>
public sealed class LoadoutView : Panel
{
    /// <summary>
    /// Hexes above the hull — one per gun slot on the ship you are flying, keyed from 1.
    ///
    /// Not a constant, because it is not the same on every ship, and nothing on the wire says
    /// what it should be: <c>Reply.Slots</c> gives a slot's id and installed system guid and
    /// never its KIND. It comes from the server profile, where you state it once. Fixing it at
    /// four numbered a three-gun ship's ability bar from 5 instead of 4, because the bar is
    /// numbered straight after the weapon hexes.
    /// </summary>
    [System.ComponentModel.DesignerSerializationVisibility(
        System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int WeaponHexes
    {
        get => _weaponHexes;
        set
        {
            int v = Math.Clamp(value, 1, 8);
            if (_weaponHexes == v) return;
            _weaponHexes = v;
            _hexes.WeaponHexes = v;
            if (_gunCount is not null) _gunCount.Value = v;
            Normalise();
            Refresh2();
        }
    }

    private int _weaponHexes = 4;

    /// <summary>The spinner showing the same number, kept in step when the count is set
    /// programmatically on a profile switch. Null until the constructor has built the toolbar.</summary>
    private NumberField? _gunCount;

    /// <summary>The ability bar. The client builds it from a nine-slot background and caps the
    /// list at ten (GUIAbilityToolbar), so ten is what we draw.</summary>
    public const int BarHexes = 10;

    public int TotalHexes => WeaponHexes + BarHexes;

    private readonly WorldState _world;
    private readonly FarmBot _bot;

    /// <summary>Read afresh every time rather than held: the declarations belong to the server
    /// profile, and picking a different server swaps the whole list underneath us.</summary>
    private readonly Func<List<SavedSlot>> _source;

    private List<SavedSlot> _slots => _source();

    private readonly HexPanel _hexes;
    private readonly SlotTable _table;

    /// <summary>Raised after any edit, so the owner can push the declarations into the weapon
    /// book and write bot.json.</summary>
    public event Action? Changed;

    public LoadoutView(WorldState world, FarmBot bot, Func<List<SavedSlot>> source)
    {
        _world = world;
        _bot = bot;
        _source = source;
        BackColor = Theme.Bg;
        Normalise();

        _table = new SlotTable(world, bot, source) { Dock = DockStyle.Fill };
        _hexes = new HexPanel(world, bot, source) { Dock = DockStyle.Top, Height = 268 };
        _hexes.HexClicked += EditHex;

        var tools = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 36,
            BackColor = Theme.Bg,
            Padding = new Padding(8, 4, 8, 4),
            WrapContents = false,
        };

        var btnImport = new FlatButton { Text = "Place known slots", Width = 132 };
        btnImport.Click += (_, _) => ImportUnplaced();

        var btnClear = new FlatButton { Text = "Clear all", Width = 78 };
        btnClear.Click += (_, _) =>
        {
            if (MessageBox.Show("Forget every slot you declared? The bot goes back to guessing.",
                    "Clear loadout", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            _slots.Clear();
            Refresh2();
        };

        // How many guns this ship has. Nothing on the wire says, so it is stated here and
        // remembered on the server profile.
        var gunCount = new NumberField(1, 8, 1, _weaponHexes, "guns") { Margin = new Padding(12, 0, 0, 0) };
        gunCount.ValueChanged += (_, _) =>
        {
            WeaponHexes = gunCount.Value;
            Changed?.Invoke();
        };

        tools.Controls.Add(btnImport);
        tools.Controls.Add(btnClear);
        tools.Controls.Add(gunCount);
        _gunCount = gunCount;

        Controls.Add(_table);
        Controls.Add(tools);
        Controls.Add(_hexes);

        _world.LoadoutChanged += OnLoadoutChanged;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _world.LoadoutChanged -= OnLoadoutChanged;
        base.Dispose(disposing);
    }

    private void OnLoadoutChanged()
    {
        if (IsDisposed) return;
        if (InvokeRequired) { try { BeginInvoke(StampGuids); } catch { } return; }
        StampGuids();
    }

    /// <summary>
    /// Records the catalogue guid the server currently reports in each declared slot.
    ///
    /// The first sighting is just bookkeeping. The second one is the point: if the guid changes
    /// under a slot you already described, you refitted, and what you typed no longer describes
    /// what is bolted on — which the table then says out loud instead of flying on a stale card.
    /// </summary>
    private void StampGuids()
    {
        foreach (var s in _slots)
        {
            if (!s.Bound) continue;
            var live = _world.MyLoadout?.Slot((ushort)s.SlotId);
            if (live is null || !live.Filled) continue;
            if (s.SystemGuid == 0) s.SystemGuid = live.SystemGuid;
        }
        Invalidate(true);
        _table.Invalidate();
        _hexes.Invalidate();
    }

    /// <summary>Every hex a declaration claims, so a slot is never in two places at once.</summary>
    private void Normalise()
    {
        var taken = new HashSet<int>();
        foreach (var s in _slots.Where(s => s.Hex >= 1 && s.Hex <= TotalHexes))
            if (!taken.Add(s.Hex)) s.Hex = 0;

        foreach (var s in _slots.Where(s => s.Hex == 0))
        {
            bool weapon = Enum.TryParse<ShipSlotType>(s.Category, true, out var cat) && SlotTypes.IsWeapon(cat);
            int from = weapon ? 1 : WeaponHexes + 1;
            int to = weapon ? WeaponHexes : TotalHexes;
            for (int h = from; h <= to; h++)
                if (taken.Add(h)) { s.Hex = h; break; }
        }

        // Anything that still found no home would be invisible; drop the hex claim rather than
        // pretend, and the table's "not placed" line will show it.
        foreach (var s in _slots.Where(s => s.Hex < 1 || s.Hex > TotalHexes)) s.Hex = 0;
    }

    /// <summary>
    /// Drops every slot the bot knows about but you haven't placed onto the first free hexes.
    ///
    /// A starting point, not an answer: the bot's guess at the role decides which group it lands
    /// in, and the whole reason this panel exists is that the guess is sometimes wrong. Fix it by
    /// clicking the hex.
    /// </summary>
    private void ImportUnplaced()
    {
        var placed = _slots.Where(s => s.Bound).Select(s => (ushort)s.SlotId).ToHashSet();
        var known = KnownSlotIds().Where(id => !placed.Contains(id)).ToList();
        if (known.Count == 0)
        {
            MessageBox.Show("Nothing to place — every slot the bot has seen is already on a hex.\n\n" +
                            "Slots show up once the server sends its stats, or once you fire them.",
                "Place known slots", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var taken = _slots.Where(s => s.Hex > 0).Select(s => s.Hex).ToHashSet();
        int added = 0;

        foreach (var id in known)
        {
            var w = _bot.Weapons.Find(id);
            bool weapon = w?.Role is WeaponRole.Combat or WeaponRole.Mining;
            int from = weapon ? 1 : WeaponHexes + 1;
            int to = weapon ? WeaponHexes : TotalHexes;

            int hex = 0;
            for (int h = from; h <= to; h++) if (taken.Add(h)) { hex = h; break; }
            // Weapon hexes full? The bar still has room, and a gun on the bar beats no gun.
            if (hex == 0) for (int h = 1; h <= TotalHexes; h++) if (taken.Add(h)) { hex = h; break; }
            if (hex == 0) break;

            _slots.Add(new SavedSlot
            {
                SlotId = id,
                Hex = hex,
                Category = weapon ? ShipSlotType.Gun.ToString() : ShipSlotType.Undefined.ToString(),
                SystemGuid = _world.MyLoadout?.Slot(id)?.SystemGuid ?? 0,
            });
            added++;
        }

        Refresh2();
        MessageBox.Show($"Placed {added} slot(s). Click each hex to say what it is.",
            "Place known slots", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>Every slot id the bot has any reason to believe exists, from all three sources.</summary>
    private List<ushort> KnownSlotIds()
    {
        var ids = new SortedSet<ushort>();
        foreach (var s in _world.MySlots()) if (s.Filled) ids.Add(s.SlotId);
        foreach (var s in _world.KnownSlots()) ids.Add(s);
        foreach (var w in _bot.Weapons.All()) ids.Add(w.AbilityId);
        return ids.ToList();
    }

    private void EditHex(int hex)
    {
        var existing = _slots.FirstOrDefault(s => s.Hex == hex);
        using var dlg = new SlotEditorDialog(_world, _bot, hex, existing, KnownSlotIds(), WeaponHexes);
        var result = dlg.ShowDialog(FindForm());

        if (result == DialogResult.Abort)               // "Clear hex"
        {
            if (existing is not null) _slots.Remove(existing);
            Refresh2();
            return;
        }
        if (result != DialogResult.OK) return;

        var edited = dlg.Result;
        // A slot id can only be in one place. Moving it to a new hex vacates the old one.
        _slots.RemoveAll(s => s != existing && s.Bound && s.SlotId == edited.SlotId);
        if (existing is not null) _slots.Remove(existing);
        _slots.Add(edited);
        Refresh2();
    }

    private void Refresh2()
    {
        Normalise();
        Changed?.Invoke();
        _hexes.Invalidate();
        _table.Invalidate();
    }

    /// <summary>Called on the UI timer so live numbers (reach, source, refit) stay current.</summary>
    public void Tick()
    {
        _hexes.Invalidate();
        _table.Invalidate();
    }

    /// <summary>Picks up a different server profile's declarations.</summary>
    public void Reload()
    {
        Normalise();
        _hexes.Invalidate();
        _table.Invalidate();
    }

    // ================================================================ the diagram

    /// <summary>The hexes themselves. Clicking one is how everything in this panel is edited.</summary>
    private sealed class HexPanel : Control
    {
        private readonly WorldState _world;
        private readonly FarmBot _bot;
        private readonly Func<List<SavedSlot>> _source;
        private List<SavedSlot> _slots => _source();
        private readonly List<(PointF Centre, float R, int Hex)> _hits = [];
        private int _hover = -1;

        public event Action<int>? HexClicked;

        /// <summary>Kept in step with the owning view — the bar numbers itself straight after
        /// the guns, so getting this wrong shifts every ability slot's label.</summary>
        [System.ComponentModel.DesignerSerializationVisibility(
            System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public int WeaponHexes
        {
            get => _weaponHexes;
            set { _weaponHexes = value; Invalidate(); }
        }

        private int _weaponHexes = 4;

        public HexPanel(WorldState world, FarmBot bot, Func<List<SavedSlot>> source)
        {
            _world = world;
            _bot = bot;
            _source = source;
            DoubleBuffered = true;
            BackColor = Theme.Bg;
            Cursor = Cursors.Hand;
        }

        /// <summary>
        /// Where the gun hexes sit, for any number of them.
        ///
        ///       [2][3]
        ///    [1]      [4]
        ///
        /// Same arch the game draws: spread symmetrically about the hull, and the further from
        /// centre a hex is the lower it hangs, so the ship reads as being between them. This was
        /// a hardcoded four-point array, which is why the count could never change.
        /// </summary>
        private static PointF[] GunPositions(int count, float cx, float top, float r)
        {
            float w = r * MathF.Sqrt(3f);
            var pts = new PointF[count];
            if (count == 1) { pts[0] = new PointF(cx, top); return pts; }

            float half = (count - 1) / 2f;
            for (int i = 0; i < count; i++)
            {
                float offset = i - half;                       // -1.5 … +1.5 for four
                float edge = MathF.Abs(offset) / half;         // 0 at the centre, 1 at the ends
                // Squared so the inner pair stays high and only the outermost really drops.
                pts[i] = new PointF(cx + offset * w * 1.15f, top + r * 1.15f * edge * edge);
            }
            return pts;
        }

        /// <summary>Pointy-top hexagon, which is the shape that tiles a row edge to edge — the
        /// same reason the game's ability bar uses it.</summary>
        private static PointF[] Hexagon(PointF c, float r)
        {
            var pts = new PointF[6];
            for (int i = 0; i < 6; i++)
            {
                float a = MathF.PI / 180f * (60 * i + 90);
                pts[i] = new PointF(c.X + r * MathF.Cos(a), c.Y - r * MathF.Sin(a));
            }
            return pts;
        }

        private static Color RoleTint(WeaponRole role) => role switch
        {
            WeaponRole.Combat => Theme.Bad,
            WeaponRole.Mining => Theme.Warn,
            WeaponRole.Scanner => Theme.Accent,
            WeaponRole.Repair => Theme.Good,
            WeaponRole.Utility => Theme.Muted,
            _ => Theme.Faint,
        };

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.Bg);
            _hits.Clear();

            int w = Width, h = Height;
            if (w < 200 || h < 120) return;

            float cx = w / 2f;

            // Bar first: it sets the scale, because ten hexes across is the tightest constraint.
            float barR = Math.Min(30f, (w - 60f) / (BarHexes * MathF.Sqrt(3f)));
            float barW = barR * MathF.Sqrt(3f);
            float barY = h - barR - 16f;

            // The weapon hexes are bigger — they are the guns, and the game draws them bigger too.
            float gunR = barR * 1.35f;
            float gunW = gunR * MathF.Sqrt(3f);
            float gunTop = 46f;

            DrawHull(g, cx, gunTop + gunR * 2.15f, gunR * 1.7f);

            var gunAt = GunPositions(WeaponHexes, cx, gunTop, gunR);
            for (int i = 0; i < gunAt.Length; i++) DrawHex(g, gunAt[i], gunR, i + 1, true);

            float barX = cx - (BarHexes - 1) * barW / 2f;
            for (int i = 0; i < BarHexes; i++)
                DrawHex(g, new PointF(barX + i * barW, barY), barR, WeaponHexes + i + 1, false);

            using var faint = new SolidBrush(Theme.Faint);
            g.DrawString("WEAPON HEXES", Theme.Header, faint, 12, 12);
            g.DrawString("ABILITY BAR", Theme.Header, faint, 12, barY - barR - 18f);

            // Both captions live in the left gutter: the middle is the diagram, and a note
            // pinned to the right edge lands on top of the outermost weapon hex.
            using var hint = new SolidBrush(Theme.Muted);
            g.DrawString("click a hex to say what is in it", Theme.UiSmall, hint, 12, 26);
            DrawShipLine(g, 12, 42);
        }

        /// <summary>A suggestion of the hull between the weapon hexes, so the diagram reads as
        /// a ship rather than as fourteen unrelated buttons.</summary>
        private static void DrawHull(Graphics g, float cx, float cy, float size)
        {
            using var body = new Pen(Color.FromArgb(40, 50, 64), 1.3f);
            float wing = size * 0.62f, nose = size * 0.5f, tail = size * 0.42f;
            g.DrawPolygon(body,
            [
                new PointF(cx, cy - nose),
                new PointF(cx + wing, cy + tail * 0.55f),
                new PointF(cx + wing * 0.32f, cy + tail),
                new PointF(cx - wing * 0.32f, cy + tail),
                new PointF(cx - wing, cy + tail * 0.55f),
            ]);
        }

        /// <summary>What the server says is on the ship, as a counterweight to what you typed.</summary>
        private void DrawShipLine(Graphics g, float x, float y)
        {
            var ship = _world.MyLoadout;
            string text = ship is null
                ? "no slot list from this server yet — declare the hexes by hand"
                : $"ship #{ship.ShipId}{(ship.Name.Length > 0 ? $" \"{ship.Name}\"" : "")}, "
                  + $"{ship.Slots().Count(s => s.Filled)} of {ship.Count} slots filled";

            using var brush = new SolidBrush(ship is null ? Theme.Warn : Theme.Faint);
            g.DrawString(text, Theme.UiSmall, brush, x, y);
        }

        private void DrawHex(Graphics g, PointF c, float r, int hex, bool isGun)
        {
            _hits.Add((c, r, hex));

            var slot = _slots.FirstOrDefault(s => s.Hex == hex);
            var weapon = slot is { Bound: true } ? _bot.Weapons.Find((ushort)slot.SlotId) : null;
            bool bound = slot is { Bound: true };
            bool hover = _hover == hex;

            var tint = weapon is null ? Theme.Faint : RoleTint(weapon.Role);
            var pts = Hexagon(c, r);

            using (var fill = new SolidBrush(bound
                       ? FlatButton.Blend(tint, Theme.Card, hover ? 0.62f : 0.8f)
                       : hover ? Theme.CardHi : Theme.Card))
                g.FillPolygon(fill, pts);

            using (var edge = new Pen(bound ? tint : hover ? Theme.Muted : Theme.Border, bound ? 1.6f : 1.2f))
            {
                if (!bound) edge.DashStyle = DashStyle.Dash;
                g.DrawPolygon(edge, pts);
            }

            // The disabled ones matter: an unticked slot is one the bot will never fire, and
            // that has to be visible without opening the editor.
            if (slot is { Enabled: false })
            {
                using var off = new Pen(Theme.Bad, 1.6f);
                g.DrawLine(off, c.X - r * 0.5f, c.Y - r * 0.5f, c.X + r * 0.5f, c.Y + r * 0.5f);
                g.DrawLine(off, c.X + r * 0.5f, c.Y - r * 0.5f, c.X - r * 0.5f, c.Y + r * 0.5f);
            }

            // Hex number, top-left corner of the cell — the game numbers them, so we do too.
            using (var num = new SolidBrush(Theme.Faint))
                g.DrawString(hex.ToString(), Theme.MonoSmall, num, c.X - r * 0.72f, c.Y - r * 0.92f);

            if (!bound)
            {
                using var empty = new SolidBrush(Theme.Faint);
                var s = g.MeasureString("empty", Theme.UiSmall);
                g.DrawString("empty", Theme.UiSmall, empty, c.X - s.Width / 2f, c.Y - s.Height / 2f);
                return;
            }

            string top = slot!.Name.Length > 0 ? Shorten(slot.Name, isGun ? 13 : 7) : $"#{slot.SlotId}";
            string bottom = weapon?.MaxRange is { } m ? $"{m:F0}u" : $"#{slot.SlotId}";
            if (slot.Name.Length == 0) bottom = weapon?.Role.ToString() ?? "";

            using var label = new SolidBrush(Theme.Text);
            using var sub = new SolidBrush(Theme.Muted);
            var ts = g.MeasureString(top, Theme.UiSmall);
            g.DrawString(top, Theme.UiSmall, label, c.X - ts.Width / 2f, c.Y - ts.Height + 1);
            var bs = g.MeasureString(bottom, Theme.MonoSmall);
            g.DrawString(bottom, Theme.MonoSmall, sub, c.X - bs.Width / 2f, c.Y + 1);
        }

        private static string Shorten(string s, int max) =>
            s.Length <= max ? s : s[..Math.Max(1, max - 1)] + "…";

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int was = _hover;
            _hover = HexAt(e.Location);
            if (was != _hover) Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hover != -1) { _hover = -1; Invalidate(); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            int hex = HexAt(e.Location);
            if (hex > 0) HexClicked?.Invoke(hex);
        }

        private int HexAt(Point p)
        {
            foreach (var (c, r, hex) in _hits)
            {
                float dx = p.X - c.X, dy = p.Y - c.Y;
                if (dx * dx + dy * dy <= r * r * 0.86f) return hex;
            }
            return -1;
        }
    }

    // ================================================================ the table

    /// <summary>
    /// What the bot ended up believing, slot by slot. The hexes are for editing; this is for
    /// checking — it puts what you declared next to what the wire says, and names which of the
    /// two is actually in force.
    /// </summary>
    private sealed class SlotTable : Control
    {
        private readonly WorldState _world;
        private readonly FarmBot _bot;
        private readonly Func<List<SavedSlot>> _source;
        private List<SavedSlot> _slots => _source();
        private int _top;

        private const int RowHeight = 17;

        public SlotTable(WorldState world, FarmBot bot, Func<List<SavedSlot>> source)
        {
            _world = world;
            _bot = bot;
            _source = source;
            DoubleBuffered = true;
            BackColor = Theme.Bg;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            _top = Math.Max(0, _top - Math.Sign(e.Delta) * 3);
            Invalidate();
        }

        /// <summary>Column heads and their share of the width. Weights rather than pixels: the
        /// centre column is whatever is left after the two rails, so a fixed table would either
        /// waste half the panel or lose its last column off the edge.</summary>
        private static readonly (string Head, float Weight, float Min)[] Columns =
        [
            ("HEX", 4, 34), ("SLOT", 5, 42), ("NAME", 17, 110), ("CATEGORY", 10, 74),
            ("ROLE", 8, 62), ("REACH", 6, 50), ("RELOAD", 6, 50), ("POWER", 5, 44),
            ("AMMO", 11, 70), ("KNOWN FROM", 22, 120),
        ];

        /// <summary>Pixel width of each column at the current panel width.</summary>
        private int[] Widths()
        {
            float total = Columns.Sum(c => c.Weight);
            float space = Math.Max(Columns.Sum(c => c.Min), Width - 20);
            return Columns.Select(c => (int)Math.Max(c.Min, space * c.Weight / total)).ToArray();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Bg);

            using var divider = new Pen(Theme.Border);
            g.DrawLine(divider, 0, 0, Width, 0);

            var widths = Widths();
            int x = 10;
            for (int i = 0; i < Columns.Length; i++)
            {
                TextRenderer.DrawText(g, Columns[i].Head, Theme.Header, new Rectangle(x, 6, widths[i], 14),
                    Theme.Faint, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
                x += widths[i];
            }
            g.DrawLine(divider, 8, 22, Width - 8, 22);

            var rows = _slots.Where(s => s.Hex > 0).OrderBy(s => s.Hex).ToList();
            int visible = Math.Max(1, (Height - 46) / RowHeight);
            _top = Math.Clamp(_top, 0, Math.Max(0, rows.Count - visible));

            int y = 26;
            foreach (var s in rows.Skip(_top).Take(visible))
            {
                DrawRow(g, s, y, widths);
                y += RowHeight;
            }

            if (rows.Count == 0)
            {
                TextRenderer.DrawText(g, "Nothing declared yet — click a hex above, or press "
                    + "\"Place known slots\" to start from what the bot has already seen.",
                    Theme.UiSmall, new Rectangle(10, 30, Width - 20, 18), Theme.Faint, TextFormatFlags.Left);
            }

            DrawFooter(g);
        }

        private void DrawRow(Graphics g, SavedSlot s, int y, int[] widths)
        {
            var w = !s.Bound ? null : _bot.Weapons.Find((ushort)s.SlotId);
            var live = !s.Bound ? null : _world.MyLoadout?.Slot((ushort)s.SlotId);
            bool refitted = s.SystemGuid != 0 && live is { Filled: true } && live.SystemGuid != s.SystemGuid;

            var text = refitted ? Theme.Warn : s.Enabled ? Theme.Text : Theme.Faint;
            var dim = refitted ? Theme.Warn : Theme.Muted;

            string reach = w?.MaxRange is { } m ? $"{m:F0}u" : "—";
            string reload = w?.Cooldown is { } c ? $"{c:F1}s" : "—";
            string power = w?.PowerCost is { } p ? $"{p:F0}" : "—";
            string source = refitted
                ? "REFITTED — the item in this slot changed"
                : live is { Inoperable: true } ? "inoperable — the server says this slot is broken"
                : w?.Source ?? "never seen on the wire";

            string[] cells =
            [
                s.Hex.ToString(),
                s.Bound ? $"#{s.SlotId}" : "—",
                s.Name.Length > 0 ? s.Name : "(unnamed)",
                s.Category,
                w is null ? "—" : w.Role.ToString() + (w.RoleFromUser ? "*" : ""),
                reach, reload, power,
                s.Ammo.Length > 0 ? s.Ammo : "—",
                source,
            ];

            int x = 10;
            for (int i = 0; i < Columns.Length; i++)
            {
                var colour = i is 0 or 1 or 9 ? dim : text;
                var font = i is 0 or 1 or 5 or 6 or 7 ? Theme.MonoSmall : Theme.UiSmall;
                TextRenderer.DrawText(g, cells[i], font, new Rectangle(x, y, widths[i] - 6, RowHeight),
                    colour, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                x += widths[i];
            }
        }

        /// <summary>The two things worth saying under the table: what the star means, and which
        /// slots the bot knows about that you have not placed.</summary>
        private void DrawFooter(Graphics g)
        {
            var placed = _slots.Where(s => s.Bound).Select(s => (ushort)s.SlotId).ToHashSet();
            var loose = _bot.Weapons.All().Select(x => x.AbilityId)
                .Concat(_world.KnownSlots())
                .Concat(_world.MySlots().Where(s => s.Filled).Select(s => s.SlotId))
                .Distinct()
                .Where(id => !placed.Contains(id))
                .OrderBy(id => id)
                .ToList();

            // Say what the bot BELIEVES each unplaced slot is, not just that it exists. A bare
            // list of ids is what hid the bug that cost three sessions: the bot had decided #4
            // was a repair module and would fire it at the hull, and the footer said "#4".
            string Describe(ushort id)
            {
                var w = _bot.Weapons.Find(id);
                return w is null || w.Role == WeaponRole.Unknown ? $"#{id}" : $"#{id}={w.Role}";
            }

            string note = loose.Count == 0
                ? "* role is yours, not a guess.   Every slot the bot knows about is placed."
                : $"* role is yours, not a guess.   Not placed, so NOT used: "
                  + string.Join(" ", loose.Take(10).Select(Describe))
                  + (loose.Count > 10 ? $" +{loose.Count - 10} more" : "");

            TextRenderer.DrawText(g, note, Theme.UiSmall,
                new Rectangle(10, Height - 18, Width - 20, 16),
                loose.Count == 0 ? Theme.Faint : Theme.Muted,
                TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        }
    }
}
