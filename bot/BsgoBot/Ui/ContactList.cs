using System.Numerics;
using BsgoBot.Bot;
using BsgoBot.Protocol;
using BsgoBot.World;

namespace BsgoBot.Ui;

/// <summary>
/// Every contact in the sector as a sorted, filterable list.
///
/// The tactical map answers "where is it"; this answers "what is out there", which is the
/// question you actually have most of the time — and unlike a dot on a projection, a row can be
/// clicked, sorted and read. The server streams the whole sector, so this list is the entire
/// sector, including the contacts your own client filters out before it draws anything.
/// </summary>
public sealed class ContactList : Panel
{
    private readonly WorldState _world;
    private readonly FarmBot _bot;
    private readonly GameActions _act;

    private readonly Table _table;
    private readonly TextField _search = new(placeholder: "filter by name, type or id");
    private readonly List<ToggleChip> _filters = [];
    private readonly Label _summary = new();

    private readonly FlatButton _btnPin = new();
    private readonly FlatButton _btnGoTo = new();
    private readonly FlatButton _btnFollow = new();
    private readonly FlatButton _btnLock = new();
    private readonly FlatButton _btnLoot = new();
    private readonly FlatButton _btnDock = new();
    private readonly FlatButton _btnWhoIs = new();

    /// <summary>Raised when you click a row, so the map can put a ring round the same contact.</summary>
    public event Action<uint>? SelectionChanged;

    [System.ComponentModel.DesignerSerializationVisibility(
        System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public uint Selected
    {
        get => _table.Selected;
        set { if (_table.Selected != value) { _table.Selected = value; _table.Invalidate(); } }
    }

    /// <summary>The groups the filter chips switch on and off.</summary>
    private static readonly (string Name, SpaceEntityType[] Types)[] Groups =
    [
        ("Players", [SpaceEntityType.Player]),
        ("NPCs", [SpaceEntityType.BotFighter, SpaceEntityType.AsteroidBot,
                  SpaceEntityType.MiningShip, SpaceEntityType.Cruiser]),
        ("Stations", [SpaceEntityType.Outpost, SpaceEntityType.WeaponPlatform]),
        ("Rocks", [SpaceEntityType.Asteroid, SpaceEntityType.Planetoid, SpaceEntityType.Comet]),
        ("Loot", [SpaceEntityType.CargoObject, SpaceEntityType.Debris]),
        ("Hazards", [SpaceEntityType.Missile, SpaceEntityType.Mine,
                     SpaceEntityType.SmartMine, SpaceEntityType.MineField]),
        ("Scenery", [SpaceEntityType.Planet, SpaceEntityType.Trigger, SpaceEntityType.Volume,
                     SpaceEntityType.SectorEvent, SpaceEntityType.JumpBeacon,
                     SpaceEntityType.JumpTargetTransponder, SpaceEntityType.CaptureTrigger]),
    ];

    public ContactList(WorldState world, FarmBot bot, GameActions actions)
    {
        _world = world;
        _bot = bot;
        _act = actions;
        BackColor = Theme.Bg;

        _table = new Table(world) { Dock = DockStyle.Fill };
        _table.SelectionChanged += id => { SelectionChanged?.Invoke(id); SyncButtons(); };
        _table.Activated += Pin;

        Controls.Add(_table);
        Controls.Add(BuildActions());
        Controls.Add(BuildFilters());
    }

    private Control BuildFilters()
    {
        var bar = new Panel { Dock = DockStyle.Top, Height = 64, BackColor = Theme.Bg };

        var chips = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, Height = 32, BackColor = Theme.Bg,
            Padding = new Padding(8, 5, 8, 0), WrapContents = false, AutoScroll = false,
        };

        foreach (var (name, types) in Groups)
        {
            var chip = new ToggleChip(name, true) { Tag2 = types, Margin = new Padding(0, 0, 4, 0) };
            chip.CheckedChanged += (_, _) => ApplyFilters();
            _filters.Add(chip);
            chips.Controls.Add(chip);
        }

        var row = new Panel { Dock = DockStyle.Top, Height = 32, BackColor = Theme.Bg };
        _search.Bounds = new Rectangle(8, 3, 240, 26);
        _search.Committed += (_, _) => ApplyFilters();
        _search.TextChanged += (_, _) => ApplyFilters();

        _summary.Bounds = new Rectangle(258, 3, 700, 26);
        _summary.Font = Theme.MonoSmall;
        _summary.ForeColor = Theme.Faint;
        _summary.TextAlign = ContentAlignment.MiddleLeft;

        row.Controls.Add(_search);
        row.Controls.Add(_summary);

        bar.Controls.Add(row);
        bar.Controls.Add(chips);
        return bar;
    }

    /// <summary>
    /// What you can do to the selected contact.
    ///
    /// Wraps rather than runs off the edge: at the window's minimum width the centre column is
    /// only a few hundred pixels, and a row of eight buttons that silently loses its last three
    /// is worse than two rows.
    /// </summary>
    private Control BuildActions()
    {
        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = 76, BackColor = Theme.Panel,
            Padding = new Padding(8, 6, 8, 6), WrapContents = true, AutoScroll = false,
        };

        _btnPin.Text = "Pin as target";
        _btnPin.Width = 108;
        _btnPin.Primary = true;
        _btnPin.Click += (_, _) => Pin(Selected);

        var unpin = new FlatButton { Text = "Unpin", Width = 66, Margin = new Padding(0, 0, 12, 4) };
        unpin.Click += (_, _) => _bot.Unpin();

        // Fly there and stop. Ends itself on arrival.
        _btnGoTo.Text = "Go to";
        _btnGoTo.Width = 66;
        _btnGoTo.Click += (_, _) => { if (Selected != 0) _bot.FlyTo(Selected, keepStation: false); SyncButtons(); };

        // Fly there and stay. Doubles as the cancel for either kind of run, because a button
        // that starts something should be the one that stops it.
        _btnFollow.Text = "Follow";
        _btnFollow.Width = 104;
        _btnFollow.Margin = new Padding(0, 0, 12, 4);
        _btnFollow.Click += (_, _) =>
        {
            if (_bot.IsFollowing) _bot.StopFollowing();
            else if (Selected != 0) _bot.FlyTo(Selected, keepStation: true);
            SyncButtons();
        };

        _btnLock.Text = "Lock";
        _btnLock.Width = 62;
        _btnLock.Click += (_, _) => Act(id => _act.LockTarget(id), "lock");

        _btnLoot.Text = "Loot";
        _btnLoot.Width = 62;
        _btnLoot.Click += (_, _) => Act(id => _act.RequestLoot(id), "loot");

        _btnDock.Text = "Dock here";
        _btnDock.Width = 86;
        _btnDock.Click += (_, _) => Act(id => _act.Dock(id), "dock at");

        _btnWhoIs.Text = "Ask WhoIs";
        _btnWhoIs.Width = 90;
        _btnWhoIs.Margin = new Padding(12, 0, 0, 4);
        _btnWhoIs.Click += (_, _) => Act(id => _act.WhoIs(id), "ask about");

        foreach (var b in new[] { _btnPin, unpin, _btnGoTo, _btnFollow, _btnLock, _btnLoot, _btnDock, _btnWhoIs })
        {
            if (b.Margin == new Padding(3)) b.Margin = new Padding(0, 0, 5, 4);
            bar.Controls.Add(b);
        }

        SyncButtons();
        return bar;
    }

    private void Pin(uint id)
    {
        if (id == 0) return;
        _bot.Pin(id);
        SyncButtons();
    }

    private void Act(Func<uint, Task> what, string verb)
    {
        uint id = Selected;
        if (id == 0) return;
        try { _ = what(id); }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not {verb} #{id:X8}: {ex.Message}\n\n" +
                            "The game client has to be connected through the proxy for this.",
                "No session", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    /// <summary>Only offer an action that makes sense for what is selected.</summary>
    private void SyncButtons()
    {
        uint id = Selected;
        bool any = id != 0;

        // Flying somewhere needs somewhere to fly to: an object we know exists but have never
        // been given a position for is not a destination, it is a rumour.
        bool located = any && _world.Get(id) is { HasPosition: true };

        _btnPin.Enabled = any;
        _btnLock.Enabled = any;
        _btnWhoIs.Enabled = any;
        _btnLoot.Enabled = any && EntityTypes.IsLootable(id);
        _btnDock.Enabled = any && EntityTypes.IsDockable(id);
        _btnGoTo.Enabled = located && !_bot.IsFollowing;

        // The button that starts a run is the one that stops it, whichever row is selected —
        // otherwise cancelling means first hunting down the contact you set off after.
        bool running = _bot.IsFollowing;
        _btnFollow.Enabled = running || located;
        string want = running ? "Stop following" : "Follow";
        if (_btnFollow.Text != want) { _btnFollow.Text = want; _btnFollow.Invalidate(); }
        _btnFollow.Tint = running ? Theme.Bad : Color.Empty;
    }

    private void ApplyFilters()
    {
        var allowed = new HashSet<SpaceEntityType>();
        foreach (var chip in _filters)
            if (chip.Checked && chip.Tag2 is SpaceEntityType[] types)
                foreach (var t in types) allowed.Add(t);

        // "Scenery" also stands for everything with no chip of its own, so an unusual object
        // type is never silently invisible.
        bool sceneryOn = _filters[^1].Checked;
        _table.SetFilter(allowed, sceneryOn, _search.Text.Trim());
    }

    /// <summary>Called on the UI timer. Rebuilding here rather than in OnPaint keeps the row
    /// order stable between a click and the redraw that follows it.</summary>
    public void Tick()
    {
        _table.Rebuild();
        _summary.Text = _table.Summary;
        SyncButtons();
    }

    // ================================================================ the table

    private sealed class Table : Control
    {
        private readonly WorldState _world;
        private readonly VScrollBar _scroll = new();
        private List<Row> _rows = [];
        private HashSet<SpaceEntityType> _allowed = [.. Enum.GetValues<SpaceEntityType>()];
        private bool _scenery = true;
        private string _search = "";
        private int _top;
        private int _sort = 4;                 // distance
        private bool _descending;
        private readonly List<Rectangle> _headerHits = [];

        private const int RowHeight = 18;
        private const int HeaderHeight = 24;

        [System.ComponentModel.DesignerSerializationVisibility(
            System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public uint Selected { get; set; }

        public string Summary { get; private set; } = "";

        public event Action<uint>? SelectionChanged;
        public event Action<uint>? Activated;

        private sealed record Row(
            uint Id, SpaceEntityType Type, string Name, Relation Rel, float? Dist,
            ContactLayer Band, string Health, string Note, bool Mine);

        /// <summary>Column heads with their share of the width, plus a floor. Weights rather
        /// than pixels because the centre column is whatever the two rails leave behind, and a
        /// fixed table loses NOTES off the right edge on a smaller window.</summary>
        private static readonly (string Head, float Weight, float Min)[] Columns =
        [
            ("TYPE", 12, 84), ("NAME", 18, 110), ("SIDE", 7, 54), ("ID", 9, 74),
            ("RANGE", 8, 66), ("BAND", 7, 58), ("HULL", 7, 54), ("NOTES", 32, 130),
        ];

        private int[] Widths(int available)
        {
            float total = Columns.Sum(c => c.Weight);
            float space = Math.Max(Columns.Sum(c => c.Min), available - 20);
            return Columns.Select(c => (int)Math.Max(c.Min, space * c.Weight / total)).ToArray();
        }

        public Table(WorldState world)
        {
            _world = world;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
            TabStop = true;

            _scroll.Dock = DockStyle.Right;
            _scroll.Width = 12;
            _scroll.Scroll += (_, _) => { _top = _scroll.Value; Invalidate(); };
            Controls.Add(_scroll);
        }

        public void SetFilter(HashSet<SpaceEntityType> allowed, bool scenery, string search)
        {
            _allowed = allowed;
            _scenery = scenery;
            _search = search;
            Rebuild();
        }

        // ------------------------------------------------------------ model

        public void Rebuild()
        {
            var det = _world.Detection;
            var now = DateTime.UtcNow;
            var objects = _world.Snapshot();
            var rows = new List<Row>(objects.Count);
            int hidden = 0;

            foreach (var o in objects)
            {
                var known = Enum.IsDefined(typeof(SpaceEntityType), o.Type);
                bool wanted = _allowed.Contains(o.Type) || (_scenery && !known);
                if (!wanted) { hidden++; continue; }

                string name = NameOf(o);
                if (_search.Length > 0 && !Matches(o, name)) { hidden++; continue; }

                rows.Add(new Row(
                    o.Id, o.Type, name,
                    o.IsMe ? Relation.Self : _world.RelationTo(o.Id),
                    _world.DistanceToMe(o),
                    o.HasPosition ? _world.LayerOf(o, det) : ContactLayer.Unknown,
                    o.StatsKnown ? $"{o.Hull:F0}" : "",
                    NoteFor(o, now),
                    o.IsMe));
            }

            Sort(rows);
            _rows = rows;

            int located = rows.Count(r => r.Dist is not null);
            Summary = $"{rows.Count} shown · {located} located · {hidden} filtered out"
                    + (det.Known ? $" · dradis {det.Dradis:F0}u map {det.Map:F0}u" : " · no detection radii");

            SyncScroll();
            Invalidate();
        }

        private bool Matches(SpaceObj o, string name) =>
            name.Contains(_search, StringComparison.OrdinalIgnoreCase)
            || o.Type.ToString().Contains(_search, StringComparison.OrdinalIgnoreCase)
            || o.Id.ToString("X8").Contains(_search, StringComparison.OrdinalIgnoreCase);

        private string NameOf(SpaceObj o)
        {
            if (o.IsMe) return "YOU";
            if (_world.NameOf(o) is { } player) return player;
            if (o.Type == SpaceEntityType.Player)
                return o.PlayerId == 0 ? "player" : $"player {o.PlayerId}";
            return o.Type.ToString();
        }

        /// <summary>The one line of extra fact worth carrying per contact — different for each
        /// kind, because "hull 40%" and "Tylium x900" answer the same question about different
        /// things: is this worth going to.</summary>
        private string NoteFor(SpaceObj o, DateTime now)
        {
            var bits = new List<string>();

            if (o.Cloaked) bits.Add("cloaked");
            if (o.InCombat) bits.Add("in combat");
            if (o.TargetId != 0 && o.TargetId == _world.MyObjectId) bits.Add("TARGETING YOU");
            if (!o.HasPosition) bits.Add("no position yet");

            if (o.Scanned)
            {
                string res = Enum.IsDefined(typeof(ResourceType), o.ResourceGuid)
                    ? ((ResourceType)o.ResourceGuid).ToString()
                    : $"resource {o.ResourceGuid}";
                bits.Add(o.IsMinable ? $"{res} x{o.ResourceCount}" : $"{res} (not minable)");
                if (o.MiningCooldown > now) bits.Add($"cooling {(o.MiningCooldown - now).TotalSeconds:F0}s");
            }
            else if (EntityTypes.IsMinable(o.Id)) bits.Add("unscanned");

            if (o.CargoAction != CargoInteraction.None) bits.Add(o.CargoAction.ToString().ToLowerInvariant());
            if (o.Radius > 0 && EntityTypes.IsStatic(o.Id)) bits.Add($"r{o.Radius:F0}");

            if (o.Velocity.LengthSquared() > 1f) bits.Add($"{o.Velocity.Length():F0}u/s");

            double age = (now - o.LastSeen).TotalSeconds;
            if (age > 30 && !EntityTypes.IsStatic(o.Id)) bits.Add($"stale {age:F0}s");

            return string.Join(", ", bits);
        }

        private void Sort(List<Row> rows)
        {
            Comparison<Row> by = _sort switch
            {
                0 => (a, b) => string.CompareOrdinal(a.Type.ToString(), b.Type.ToString()),
                1 => (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
                2 => (a, b) => a.Rel.CompareTo(b.Rel),
                3 => (a, b) => a.Id.CompareTo(b.Id),
                // Unlocated contacts sort last whichever way the column is pointing: "no
                // position" is not a distance, and letting it read as zero put them on top.
                4 => (a, b) => (a.Dist ?? float.MaxValue).CompareTo(b.Dist ?? float.MaxValue),
                5 => (a, b) => a.Band.CompareTo(b.Band),
                6 => (a, b) => string.CompareOrdinal(a.Health, b.Health),
                _ => (a, b) => string.CompareOrdinal(a.Note, b.Note),
            };

            rows.Sort((a, b) =>
            {
                if (a.Mine != b.Mine) return a.Mine ? -1 : 1;      // your own ship pinned to the top
                int c = by(a, b);
                return _descending ? -c : c;
            });
        }

        // ------------------------------------------------------------ paint

        private int VisibleRows => Math.Max(1, (Height - HeaderHeight) / RowHeight);

        private void SyncScroll()
        {
            if (!IsHandleCreated) return;
            int max = Math.Max(0, _rows.Count - VisibleRows);
            _top = Math.Clamp(_top, 0, max);
            _scroll.Maximum = Math.Max(0, _rows.Count - 1);
            _scroll.LargeChange = Math.Max(1, VisibleRows);
            _scroll.Value = Math.Clamp(_top, 0, Math.Max(0, _scroll.Maximum));
            _scroll.Visible = max > 0;
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); SyncScroll(); }

        private static Color SideColour(Relation r) => r switch
        {
            Relation.Enemy => Theme.Bad,
            Relation.Friend => Theme.Good,
            Relation.Self => Theme.Accent,
            _ => Theme.Muted,
        };

        private static Color BandColour(ContactLayer l) => l switch
        {
            ContactLayer.Visual => Color.FromArgb(150, 235, 255),
            ContactLayer.Dradis => Color.FromArgb(90, 200, 230),
            ContactLayer.Map => Color.FromArgb(80, 140, 170),
            ContactLayer.Dark => Theme.Warn,
            _ => Theme.Faint,
        };

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Bg);

            int width = Width - (_scroll.Visible ? _scroll.Width : 0);
            var widths = Widths(width);
            DrawHeader(g, widths);

            int y = HeaderHeight;

            foreach (var row in _rows.Skip(_top).Take(VisibleRows))
            {
                bool selected = row.Id == Selected;
                if (selected)
                    using (var sel = new SolidBrush(Theme.AccentDeep))
                        g.FillRectangle(sel, 0, y, width, RowHeight);

                int x = 10;
                string[] cells =
                [
                    row.Type.ToString(),
                    row.Name,
                    row.Mine ? "you" : row.Rel.ToString().ToLowerInvariant(),
                    $"#{row.Id:X8}",
                    row.Dist is { } d ? $"{d:F0}u" : "—",
                    row.Band == ContactLayer.Unknown ? "—" : Visibility.Describe(row.Band),
                    row.Health.Length > 0 ? row.Health : "—",
                    row.Note,
                ];

                for (int i = 0; i < Columns.Length; i++)
                {
                    var colour = i switch
                    {
                        2 => SideColour(row.Rel),
                        5 => BandColour(row.Band),
                        7 => row.Note.Contains("TARGETING") ? Theme.Bad : Theme.Muted,
                        3 => Theme.Faint,
                        _ => selected ? Theme.Text : Theme.Text,
                    };
                    var font = i is 3 or 4 or 6 ? Theme.MonoSmall : Theme.UiSmall;

                    TextRenderer.DrawText(g, cells[i], font,
                        new Rectangle(x, y, widths[i] - 8, RowHeight), colour,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                    x += widths[i];
                }

                y += RowHeight;
            }

            if (_rows.Count == 0)
                TextRenderer.DrawText(g, "Nothing matches. Turn a filter back on, or fly into a sector.",
                    Theme.UiSmall, new Rectangle(10, HeaderHeight + 6, Width - 20, 18),
                    Theme.Faint, TextFormatFlags.Left);
        }

        private void DrawHeader(Graphics g, int[] widths)
        {
            _headerHits.Clear();
            using var line = new Pen(Theme.Border);
            g.DrawLine(line, 0, HeaderHeight - 1, Width, HeaderHeight - 1);

            int x = 10;
            for (int i = 0; i < Columns.Length; i++)
            {
                _headerHits.Add(new Rectangle(x - 4, 0, widths[i], HeaderHeight - 1));

                string head = Columns[i].Head + (_sort == i ? _descending ? " v" : " ^" : "");
                TextRenderer.DrawText(g, head, Theme.Header, new Rectangle(x, 6, widths[i] - 8, 14),
                    _sort == i ? Theme.Accent : Theme.Faint,
                    TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
                x += widths[i];
            }
        }

        // ------------------------------------------------------------ interaction

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            _top = Math.Clamp(_top - Math.Sign(e.Delta) * 3, 0, Math.Max(0, _rows.Count - VisibleRows));
            SyncScroll();
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();

            if (e.Y < HeaderHeight)
            {
                for (int i = 0; i < _headerHits.Count; i++)
                {
                    if (!_headerHits[i].Contains(e.Location)) continue;
                    if (_sort == i) _descending = !_descending;
                    else { _sort = i; _descending = false; }
                    Rebuild();
                    return;
                }
                return;
            }

            int index = _top + (e.Y - HeaderHeight) / RowHeight;
            if (index < 0 || index >= _rows.Count) return;
            Selected = _rows[index].Id;
            SelectionChanged?.Invoke(Selected);
            Invalidate();
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Y >= HeaderHeight && Selected != 0) Activated?.Invoke(Selected);
        }

        protected override bool IsInputKey(Keys keyData) =>
            keyData is Keys.Up or Keys.Down or Keys.Enter || base.IsInputKey(keyData);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (_rows.Count == 0) return;

            int at = _rows.FindIndex(r => r.Id == Selected);
            switch (e.KeyCode)
            {
                case Keys.Up: at = Math.Max(0, at < 0 ? 0 : at - 1); break;
                case Keys.Down: at = Math.Min(_rows.Count - 1, at + 1); break;
                case Keys.Enter when Selected != 0: Activated?.Invoke(Selected); return;
                default: return;
            }

            Selected = _rows[at].Id;
            if (at < _top) _top = at;
            else if (at >= _top + VisibleRows) _top = at - VisibleRows + 1;
            SyncScroll();
            SelectionChanged?.Invoke(Selected);
            Invalidate();
            e.Handled = true;
        }
    }
}
