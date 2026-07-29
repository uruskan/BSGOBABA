using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Text.Json;
using BsgoBot.Bot;
using BsgoBot.Protocol;
using BsgoBot.Proxy;
using BsgoBot.World;

namespace BsgoBot.Ui;

public sealed class MainForm : Form
{
    private readonly Config _cfg = Config.Load();
    private readonly WorldState _world = new();
    private readonly GameProxy _proxy;
    private readonly GameActions _actions;
    private readonly FarmBot _bot;

    private readonly MapPanel _map;
    private readonly ContactList _contacts;
    private readonly LoadoutView _loadout;
    private readonly LogView _log = new();
    private readonly StatList _link = new();
    private readonly StatList _sector = new();
    private readonly StatList _session = new();
    private readonly DiagView _diagView = new() { Dock = DockStyle.Fill };
    private readonly SessionsView _sessionsView = new() { Dock = DockStyle.Fill };

    private readonly FlatButton _btnProxy = new();
    private readonly FlatButton _btnFarm = new();
    private readonly FlatButton _btnLaunch = new();
    private readonly FlatButton _btnDock = new();
    private readonly FlatButton _btnUndock = new();
    private readonly FlatButton _btnProfiles = new();
    private readonly FlatButton _btnCatch = new();
    private readonly FlatButton _btnSecond = new();
    private readonly SessionCatcher _catcher = new();
    private readonly DarkCombo _serverBox = new();
    private readonly DarkCombo _clientBox = new();
    private readonly DarkCombo _shipBox = new();
    private readonly FlatButton _btnAddShip = new();
    private readonly List<ToggleChip> _resourceChips = [];

    private readonly ToggleChip _chipCombat = new("Combat", true);
    private readonly ToggleChip _chipMining = new("Mining");
    private readonly ToggleChip _chipApproach = new("Fly to target");
    private readonly ToggleChip _chipBoost = new("Boost");
    private readonly ToggleChip _chipLoot = new("Auto loot");
    private readonly ToggleChip _chipPlayers = new("Attack players");
    private readonly ToggleChip _chipGunsOnRocks = new("Guns on rocks");
    private readonly ToggleChip _chipOptimal = new("Hold for optimal");
    private readonly ToggleChip _chipAvoidStations = new("Avoid stations");
    private readonly ToggleChip _chipRepair = new("Self repair");
    private readonly ToggleChip _chipAutoUndock = new("Auto undock");
    private readonly ToggleChip _chipHangarRepair = new("Repair in hangar");
    private readonly ToggleChip _chipDefend = new("Fight back");
    private readonly ToggleChip _chipAvoidRocks = new("Dodge obstacles");
    private readonly ToggleChip _chipCatalogue = new("Fetch cards");
    private readonly ToggleChip _chipAvoidPlayers = new("Dodge players");
    private readonly List<ToggleChip> _preyChips = [];

    private NumberField _numRange = null!;
    private NumberField _numRetreat = null!;
    private NumberField _numRock = null!;
    private NumberField _numSpeed = null!;
    private NumberField _numBoost = null!;
    private NumberField _numKeepOut = null!;
    private NumberField _numTravel = null!;
    private NumberField _numHull = null!;
    private NumberField _numNpcKeepOut = null!;
    private NumberField _numLocalTravel = null!;
    private NumberField _numTargetSector = null!;
    private readonly FlatButton _btnSectorHere = new();
    private NumberField _numShipClass = null!;

    private Panel _header = null!;
    private Panel _viewHost = null!;
    private readonly List<(string Title, ToggleChip Chip, Control View)> _views = [];
    private bool _switchingView;

    /// <summary>Height each rail card needs, worked out by the card itself while it is built.
    /// The rail clips rather than grows, so these must come from the content, not from memory.</summary>
    private int _connectionHeight, _controlHeight, _tuningHeight;

    /// <summary>Stand-in list for when no server profile is selected, so the loadout panel is
    /// still constructible instead of needing a null check on every draw.</summary>
    private readonly List<SavedSlot> _noSlots = [];

    private bool _proxyRunning;
    private bool _suppressProfileEvents;
    private string _serverReachable = "checking";

    /// <summary>NPC kinds worth offering as prey — the things that actually spawn to farm.</summary>
    private static readonly SpaceEntityType[] PreyChoices =
    [
        SpaceEntityType.BotFighter,
        SpaceEntityType.AsteroidBot,
        SpaceEntityType.MiningShip,
        SpaceEntityType.Cruiser,
        SpaceEntityType.Outpost,
        SpaceEntityType.WeaponPlatform,
    ];

    public MainForm()
    {
        // Two instances farming two servers look identical without this; the config file name
        // and the loopback address are the only things that tell the windows apart.
        Text = $"BSGO Farm Bot — {Path.GetFileName(Config.FilePath)} @ {_cfg.ListenHost}";
        ClientSize = new Size(1400, 900);
        MinimumSize = new Size(1080, 740);
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.Ui;
        StartPosition = FormStartPosition.CenterScreen;
        DoubleBuffered = true;

        _proxy = new GameProxy(
            _cfg.CurrentServer?.Host ?? "127.0.0.1",
            _cfg.CurrentServer?.Port ?? 27060);
        _actions = new GameActions(_proxy);
        _bot = new FarmBot(_world, _actions, _proxy);
        _map = new MapPanel(_world, _bot) { Dock = DockStyle.Fill };
        _contacts = new ContactList(_world, _bot, _actions) { Dock = DockStyle.Fill };
        _loadout = new LoadoutView(_world, _bot, () => _cfg.CurrentServer?.CurrentShip.Slots ?? _noSlots)
        {
            Dock = DockStyle.Fill,
        };

        // One selection, two views of it: clicking a dot on the map highlights the row, and
        // clicking the row rings the dot. Anything else means two panels quietly disagreeing
        // about what you are looking at.
        _map.ContactPicked += id => _contacts.Selected = id;
        _map.ContactActivated += id => { _contacts.Selected = id; _bot.Pin(id); };
        _contacts.SelectionChanged += id => { _map.Selected = id; _map.Invalidate(); };
        _contacts.Log += AppendLog;
        _loadout.Changed += () =>
        {
            if (_cfg.CurrentServer is { } srv) srv.CurrentShip.WeaponHexes = _loadout.WeaponHexes;
            ApplyLoadout();
            _cfg.Save();
        };

        // History lives beside the profile that produced it — bot.json gets bot.sessions.json —
        // so two instances farming two servers keep separate records.
        _bot.Sessions.Open(Path.Combine(
            Path.GetDirectoryName(Config.FilePath) ?? AppContext.BaseDirectory,
            Path.GetFileNameWithoutExtension(Config.FilePath) + ".sessions.json"));

        _proxy.Log += AppendLog;
        _bot.Log += AppendLog;
        _world.Log += AppendLog;
        _catcher.Log += AppendLog;
        _proxy.ClientEndedSession += SnapshotClientLog;
        _catcher.Captured += AdoptCapturedSession;

        // When this instance is already parked on a live server, sessions for any other live
        // server are someone else's: either a second bot instance is watching for them, or the
        // user picked the wrong window. Local profiles accept anything — that is the bootstrap
        // path, before a live server has ever been captured here.
        _catcher.AcceptHost = host =>
            _cfg.CurrentServer is not { } cur
            || cur.Host.StartsWith("127.")
            || cur.Host == "localhost"
            || cur.Host == host;

        ApplySettingsToBot();
        AdoptServerIdentity();
        BuildLayout();

        var ui = new System.Windows.Forms.Timer { Interval = 250 };
        ui.Tick += (_, _) => RefreshUiTimed();
        ui.Start();

        Load += (_, _) =>
        {
            _ = CheckServerAsync();
            if (_cfg.AutoStartProxy) ToggleProxy();
        };

        FormClosing += (_, _) =>
        {
            // Closes the live session record with a real end time; without this a run cut
            // short by closing the window reads as a crash on the next load.
            if (_bot.Enabled) _bot.Stop();
            SaveWeapons();
            _cfg.Save();
            _catcher.Stop();
            _proxy.Stop();
        };
    }

    /// <summary>
    /// DWM only honours the dark-caption attributes once the window exists, and a window that
    /// is already visible needs a second nudge before it repaints the frame.
    /// </summary>
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.UseDarkTitleBar(this);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Theme.UseDarkTitleBar(this);
    }

    // ---------------------------------------------------------------- identity & settings

    /// <summary>
    /// Hands the world model the player id the launcher passes as +userID, so the bot can
    /// recognise its own ship the moment its WhoIs arrives instead of waiting for one.
    /// </summary>
    private void AdoptServerIdentity()
    {
        var s = _cfg.CurrentServer;
        if (s is null) return;

        if (s.NumericPlayerId != 0)
            _world.SeedPlayerId(s.NumericPlayerId, "your server profile");

        _bot.Weapons.Restore(s.CurrentShip.Weapons.Select(w =>
            ((ushort)w.AbilityId, (WeaponKind)w.Kind, (WeaponRole)w.Role, w.Enabled)));

        // Gun count is per ship, so it travels with the profile. Set before the declarations are
        // pushed: it decides which hexes count as weapon hexes and how the bar is numbered.
        _loadout.WeaponHexes = s.CurrentShip.WeaponHexes;

        // After Restore, not before: what you declared outranks what was merely remembered.
        ApplyLoadout();
    }

    /// <summary>
    /// Pushes the loadout you described into the weapon book.
    ///
    /// Runs on every edit and on every profile switch, and hands over the whole list rather than
    /// the change — clearing a hex has to release its declaration, which an incremental update
    /// has no way to express.
    /// </summary>
    private void ApplyLoadout()
    {
        var s = _cfg.CurrentServer;
        if (s is null) return;

        _bot.Weapons.SyncDeclarations(s.CurrentShip.Slots
            .Where(d => d.Bound)           // a labelled hex with nothing behind it
            .Select(d =>
            {
                Enum.TryParse<ShipSlotType>(d.Category, true, out var category);
                WeaponRole? role = Enum.TryParse<WeaponRole>(d.Role, true, out var r) ? r : null;
                return new SlotDeclaration(
                    (ushort)d.SlotId, d.Name, category, d.Level, role,
                    d.MaxRange, d.OptimalRange, d.MinRange, d.Cooldown, d.PowerCost,
                    d.Ammo, d.Enabled, d.SystemGuid);
            })
            .ToList());
    }

    private void SaveWeapons()
    {
        var s = _cfg.CurrentServer;
        if (s is null) return;
        s.CurrentShip.Weapons = _bot.Weapons.All().Select(w => new SavedWeapon
        {
            AbilityId = w.AbilityId,
            Kind = (int)w.Kind,
            Role = (int)w.Role,
            Enabled = w.Enabled,
        }).ToList();
    }

    /// <summary>
    /// Hands the bot its tuning.
    ///
    /// One assignment, not 77. This used to copy every setting across by hand, which meant a new
    /// tunable was three edits — Config, here, and FarmBot — and thirty of the bot's settings had
    /// simply never been given an entry, so bot.json could not reach them at all.
    ///
    /// The bot now flies on the very object the config holds, so a change made anywhere is live
    /// immediately and <see cref="Config.Save"/> writes exactly what the bot is using. The
    /// migrations that used to sit here moved to <c>Config.MigrateTuning</c>, which runs on load
    /// and is where anything reading an outdated bot.json belongs.
    /// </summary>
    private void ApplySettingsToBot() => _bot.T = _cfg.Tuning;

    /// <summary>
    /// Rebuilds the mining filter from the chips, preserving the order they were switched ON —
    /// that order is the priority, so it cannot be re-derived from the chip layout.
    /// </summary>
    private void ApplyResources()
    {
        // No copy back into the config: _bot.T and _cfg.Tuning are the same object now.
        foreach (var chip in _resourceChips) RankChip(chip);
    }

    /// <summary>Shows a chip's place in the priority order, because "first picked wins" is
    /// invisible otherwise.</summary>
    private void RankChip(ToggleChip chip)
    {
        if (chip.Tag2 is not ResourceType r) return;
        int rank = _bot.T.WantedResources.IndexOf(r);
        chip.Text = rank >= 0 ? $"{rank + 1}. {r}" : r.ToString();
    }

    private void ApplyPrey()
    {
        _bot.T.Prey.Clear();
        foreach (var chip in _preyChips)
            if (chip.Checked && chip.Tag2 is SpaceEntityType t) _bot.T.Prey.Add(t);
    }

    // ---------------------------------------------------------------- layout

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Bg,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(0),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));   // header
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // everything else

        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildContent(), 0, 1);

        Controls.Add(root);
        RefreshProfileBoxes();
    }

    private Panel BuildHeader()
    {
        _header = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Panel };
        _header.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.Panel);

            using (var line = new Pen(Theme.Border))
                g.DrawLine(line, 0, _header.Height - 1, _header.Width, _header.Height - 1);

            // Mark: a ring with a dot, the same language as a contact on the map.
            using (var ring = new Pen(Theme.Accent, 1.6f))
                g.DrawEllipse(ring, 18, 18, 15, 15);
            using (var dot = new SolidBrush(Theme.Accent))
                g.FillEllipse(dot, 23.5f, 23.5f, 4, 4);

            using (var title = new SolidBrush(Theme.Text))
                Theme.DrawTracked(g, "BSGO FARM BOT", Theme.UiBold, title, 46, 13, 1.8f);
            using (var sub = new SolidBrush(Theme.Faint))
                g.DrawString("proxy · sector intel · autofarm", Theme.UiSmall, sub, 46, 28);

            DrawStatusPill(g, _header.Width);
        };
        return _header;
    }

    /// <summary>The one thing you look at to know what the bot is doing right now.</summary>
    private void DrawStatusPill(Graphics g, int width)
    {
        string status = _bot.Status;
        var colour = !_bot.Enabled ? Theme.Faint
                   : status.StartsWith("Error", StringComparison.Ordinal) ? Theme.Bad
                   : status.StartsWith("HULL", StringComparison.Ordinal) ? Theme.Bad
                   : status.StartsWith("Attacking", StringComparison.Ordinal)
                     || status.StartsWith("Mining", StringComparison.Ordinal) ? Theme.Good
                   : status.StartsWith("Closing", StringComparison.Ordinal) ? Theme.Accent
                   : Theme.Warn;

        var size = g.MeasureString(status, Theme.UiSmall);
        float w = Math.Min(size.Width + 34, width - 380);
        var r = new RectangleF(width - w - 18, 12, w, 26);

        Theme.FillRounded(g, r, 13f, FlatButton.Blend(colour, Theme.Card, 0.86f));
        Theme.DrawRounded(g, r, 13f, FlatButton.Blend(colour, Theme.Border, 0.35f));

        using (var dot = new SolidBrush(colour))
            g.FillEllipse(dot, r.X + 11, r.Y + 10.5f, 6, 6);

        TextRenderer.DrawText(g, status, Theme.UiSmall,
            new Rectangle((int)r.X + 23, (int)r.Y, (int)r.Width - 30, (int)r.Height),
            colour, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    /// <summary>
    /// Connection lives in the left rail as a stack. It used to be a single row 900px wide, which
    /// is what forced the window to be enormous before anything useful was on screen.
    /// </summary>
    private Control BuildConnectionCard()
    {
        var card = new Card("Connection") { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 8) };

        _serverBox.SelectedIndexChanged += (_, _) => OnServerChanged();
        _shipBox.SelectedIndexChanged += (_, _) => OnShipChanged();
        _btnAddShip.Text = "Add ship";
        _btnAddShip.Click += (_, _) => AddShip();
        _clientBox.SelectedIndexChanged += (_, _) =>
        {
            if (!_suppressProfileEvents && _clientBox.SelectedIndex >= 0)
                _cfg.SelectedClient = _clientBox.SelectedIndex;
        };

        _btnProfiles.Text = "Profiles";
        _btnProfiles.Click += (_, _) => EditProfiles();

        _btnProxy.Text = "Start proxy";
        _btnProxy.Click += (_, _) => ToggleProxy();

        _btnCatch.Text = "Catch session";
        _btnCatch.Click += (_, _) => ToggleCatcher();

        _btnLaunch.Text = "Launch game";
        _btnLaunch.Click += (_, _) => LaunchGame();

        _btnDock.Text = "Dock";
        _btnDock.Click += (_, _) => ToggleDock();

        _btnUndock.Text = "Undock";
        _btnUndock.Click += (_, _) => _bot.Undock();

        _btnFarm.Text = "Go farm";
        _btnFarm.Primary = true;
        _btnFarm.Enabled = false;
        _btnFarm.Click += (_, _) => ToggleFarm();

        _btnSecond.Text = "Second bot";
        _btnSecond.Click += (_, _) => LaunchSecondInstance();

        // One entry per ROW. Rows() sizes the grid from this array, so a cell past the end lands
        // in a row that does not exist and renders at zero height — which is how the ship picker
        // shipped invisible. The rail's own height is computed from the same array, so adding a
        // row here is now the only edit needed.
        int[] heights = [15, 32, 15, 32, 15, 32, 32, 32, 32, 32, 32, 38];
        _connectionHeight = CardHeight(heights);

        var grid = Rows(2, heights,
            (RailCaption("SERVER"), 2), (_serverBox, 2),
            // Picker and Add share a row: the rail is long enough to scroll already, and this is
            // the one card you stop looking at once the session is up.
            (RailCaption("SHIP"), 2), (_shipBox, 1), (_btnAddShip, 1),
            (RailCaption("CLIENT"), 2), (_clientBox, 2),
            (_btnProfiles, 1), (_btnLaunch, 1),
            (_btnCatch, 2),
            (_btnProxy, 2),
            (_btnSecond, 2),
            (_btnDock, 1), (_btnUndock, 1),
            (_btnFarm, 2));

        card.Controls.Add(grid);
        return card;
    }

    /// <summary>Mode plus every on/off switch, wrapped into the rail instead of run out sideways.</summary>
    private Control BuildControlCard()
    {
        var card = new Card("Control") { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 8) };

        // Chips wrap two to a rail width, so the height follows from how many there are. Counted,
        // not remembered: a hardcoded 268 here silently clipped "Fetch cards" off the bottom the
        // moment the fourteenth chip pushed the flow onto an eighth row, which is the same way
        // Go farm and the ship picker went missing.
        // ToggleChip is 26 tall with a 5px margin, and two fit a rail width. The mode pair gets a
        // row of its own because the separator under it spans the full width, so the rest wrap
        // beneath: 1 + ceil(13/2) = 8 rows. The old hardcoded 268 was 37px short — one row — and
        // that row was "Fetch cards", which is what learns a scanner's area flag and every
        // published range. An invisible switch is worse than a missing one.
        const int chipCount = 16, chipsPerRow = 2, chipRow = 26 + 5, separator = 1 + 3 + 5;
        int chipRows = 1 + (chipCount - chipsPerRow + chipsPerRow - 1) / chipsPerRow;
        _controlHeight = chipRows * chipRow + separator + CardChrome;
        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Card,
            WrapContents = true,
            AutoScroll = false,
        };

        // Two chips behaving as one radio pair reads better than a drop-down for two options.
        _chipCombat.Tint = Theme.Bad;
        _chipMining.Tint = Theme.Warn;
        _chipCombat.CheckedChanged += (_, _) => SetMode(FarmMode.Combat);
        _chipMining.CheckedChanged += (_, _) => SetMode(FarmMode.Mining);
        _chipCombat.Checked = _bot.T.Mode == FarmMode.Combat;
        _chipMining.Checked = _bot.T.Mode == FarmMode.Mining;

        _chipApproach.Checked = _cfg.Tuning.AutoApproach;
        _chipApproach.CheckedChanged += (_, _) =>
            _bot.T.AutoApproach = _cfg.Tuning.AutoApproach = _chipApproach.Checked;

        _chipBoost.Tint = Theme.Warn;
        _chipBoost.Checked = _cfg.Tuning.UseBoost;
        _chipBoost.CheckedChanged += (_, _) =>
            _bot.T.UseBoost = _cfg.Tuning.UseBoost = _chipBoost.Checked;

        _chipLoot.Checked = _cfg.Tuning.AutoLoot;
        _chipLoot.CheckedChanged += (_, _) =>
            _bot.T.AutoLoot = _cfg.Tuning.AutoLoot = _chipLoot.Checked;

        _chipPlayers.Tint = Theme.Bad;
        _chipPlayers.Checked = _cfg.Tuning.AttackPlayers;
        _chipPlayers.CheckedChanged += (_, _) =>
            _bot.T.AttackPlayers = _cfg.Tuning.AttackPlayers = _chipPlayers.Checked;

        _chipGunsOnRocks.Tint = Theme.Warn;
        _chipGunsOnRocks.Checked = _cfg.Tuning.FireGunsWhileMining;
        _chipGunsOnRocks.CheckedChanged += (_, _) =>
            _bot.T.FireGunsWhileMining = _cfg.Tuning.FireGunsWhileMining = _chipGunsOnRocks.Checked;

        _chipDefend.Tint = Theme.Bad;
        _chipDefend.Checked = _cfg.Tuning.DefendSelf;
        _chipDefend.CheckedChanged += (_, _) =>
            _bot.T.DefendSelf = _cfg.Tuning.DefendSelf = _chipDefend.Checked;

        _chipOptimal.Checked = _cfg.Tuning.HoldFireUntilOptimal;
        _chipOptimal.CheckedChanged += (_, _) =>
            _bot.T.HoldFireUntilOptimal = _cfg.Tuning.HoldFireUntilOptimal = _chipOptimal.Checked;

        _chipAvoidStations.Tint = Theme.Bad;
        _chipAvoidStations.Checked = _cfg.Tuning.AvoidHostileStations;
        _chipAvoidStations.CheckedChanged += (_, _) =>
            _bot.T.AvoidHostileStations = _cfg.Tuning.AvoidHostileStations = _chipAvoidStations.Checked;

        _chipRepair.Checked = _cfg.Tuning.UseRepairAbility;
        _chipRepair.CheckedChanged += (_, _) =>
            _bot.T.UseRepairAbility = _cfg.Tuning.UseRepairAbility = _chipRepair.Checked;

        // What happens after a death: launch again, and pay to patch the hull before doing it.
        _chipAutoUndock.Checked = _cfg.Tuning.AutoUndock;
        _chipAutoUndock.CheckedChanged += (_, _) =>
            _bot.T.AutoUndock = _cfg.Tuning.AutoUndock = _chipAutoUndock.Checked;

        // Warn-tinted: it is the one switch that spends a resource on its own.
        _chipHangarRepair.Tint = Theme.Warn;
        _chipHangarRepair.Checked = _cfg.Tuning.AutoRepair;
        _chipHangarRepair.CheckedChanged += (_, _) =>
            _bot.T.AutoRepair = _chipHangarRepair.Checked;

        _chipAvoidRocks.Tint = Theme.Bad;
        _chipAvoidRocks.Checked = _cfg.Tuning.AvoidCollisions;
        _chipAvoidRocks.CheckedChanged += (_, _) =>
            _bot.T.AvoidCollisions = _cfg.Tuning.AvoidCollisions = _chipAvoidRocks.Checked;

        // Warn-tinted: this is the one switch that puts traffic on the wire the real client never
        // sent, so it is the one to turn off first if the session starts dropping.
        _chipCatalogue.Tint = Theme.Warn;
        _chipCatalogue.Checked = _cfg.Tuning.FetchCatalogue;
        _chipCatalogue.CheckedChanged += (_, _) =>
            _bot.T.FetchCatalogue = _cfg.Tuning.FetchCatalogue = _chipCatalogue.Checked;

        _chipAvoidPlayers.Tint = Theme.Bad;
        _chipAvoidPlayers.Checked = _cfg.Tuning.AvoidPlayers;
        _chipAvoidPlayers.CheckedChanged += (_, _) =>
            _bot.T.AvoidPlayers = _cfg.Tuning.AvoidPlayers = _chipAvoidPlayers.Checked;

        // A break after the mode pair keeps "what am I doing" visually apart from "how".
        var brk = new Panel { Width = 10_000, Height = 1, BackColor = Theme.Card, Margin = new Padding(0, 3, 0, 5) };

        foreach (var chip in new[]
                 {
                     _chipCombat, _chipMining, brk as Control, _chipApproach, _chipBoost, _chipLoot,
                     _chipGunsOnRocks, _chipOptimal, _chipRepair, _chipAvoidRocks, _chipAvoidStations,
                     _chipDefend, _chipPlayers, _chipAvoidPlayers, _chipAutoUndock, _chipHangarRepair,
                     _chipCatalogue,
                 })
        {
            if (chip is ToggleChip c) c.Margin = new Padding(0, 0, 5, 5);
            flow.Controls.Add(chip);
        }

        card.Controls.Add(flow);
        return card;
    }

    /// <summary>The numbers. Caption left, field right, one per row — readable at rail width.</summary>
    /// <summary>
    /// One chip per mineable resource. Switching one on appends it to the priority list, so the
    /// order you click is the order the bot prefers — nothing selected means "whatever is
    /// nearest", which is what the old "Any" entry meant.
    /// </summary>
    private Control BuildResourceChips()
    {
        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Card,
            Margin = Padding.Empty,
            WrapContents = true,
            AutoScroll = false,
        };

        // Only what a rock can actually hold. Absence of any pick is what "Any" used to mean.
        foreach (var rt in Resources.Minable)
        {
            var chip = new ToggleChip(rt.ToString(), _bot.T.WantedResources.Contains(rt))
            {
                Tag2 = rt,
                Margin = new Padding(0, 0, 4, 4),
            };
            chip.CheckedChanged += (_, _) =>
            {
                if (chip.Checked) { if (!_bot.T.WantedResources.Contains(rt)) _bot.T.WantedResources.Add(rt); }
                else _bot.T.WantedResources.Remove(rt);
                ApplyResources();
            };
            _resourceChips.Add(chip);
            flow.Controls.Add(chip);
        }

        foreach (var chip in _resourceChips) RankChip(chip);
        return flow;
    }

    private Control BuildTuningCard()
    {
        var card = new Card("Tuning") { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 8) };

        _numRange = new NumberField(100, 50000, 100, (int)_cfg.Tuning.FallbackRange, "u");
        _numRange.ValueChanged += (_, _) =>
            _bot.T.FallbackRange = _cfg.Tuning.FallbackRange = _numRange.Value;

        _numRetreat = new NumberField(0, 95, 5, (int)(_cfg.Tuning.RetreatHull * 100f), "%");
        _numRetreat.ValueChanged += (_, _) =>
            _bot.T.RetreatHull = _cfg.Tuning.RetreatHull = _numRetreat.Value / 100f;

        // From the rock's surface now, so the useful range starts much lower than it used to.
        _numRock = new NumberField(0, 5000, 10, (int)_cfg.Tuning.AsteroidStandoff, "u");
        _numRock.ValueChanged += (_, _) =>
            _bot.T.AsteroidStandoff = _cfg.Tuning.AsteroidStandoff = _numRock.Value;

        // The one number about the hull the server never sends, and everything about not hitting
        // things is built on it. 0 measures the hardpoint spread, which is a lower bound.
        _numHull = new NumberField(0, 2000, 5, (int)_cfg.Tuning.HullRadius, "u");
        _numHull.ValueChanged += (_, _) =>
            _bot.T.HullRadius = _cfg.Tuning.HullRadius = _numHull.Value;

        // Drones and NPC fighters, as opposed to the platforms KEEP OFF GUNS covers. 0 turns it
        // off entirely, which is why this is a distance rather than a switch.
        _numNpcKeepOut = new NumberField(0, 20000, 100, (int)_cfg.Tuning.HostileShipKeepOut, "u");
        _numNpcKeepOut.ValueChanged += (_, _) =>
            _bot.T.HostileShipKeepOut = _cfg.Tuning.HostileShipKeepOut = _numNpcKeepOut.Value;

        // In seconds, because it is a question about time: how long a detour still counts as
        // "here" when a known rock sits further out than an unknown one.
        _numLocalTravel = new NumberField(0, 300, 5, (int)_cfg.Tuning.LocalTravelSeconds, "s");
        _numLocalTravel.ValueChanged += (_, _) =>
            _bot.T.LocalTravelSeconds = _cfg.Tuning.LocalTravelSeconds = _numLocalTravel.Value;

        // Where the farm belongs. 0 farms wherever the ship finds itself; any other value makes
        // a respawn or undock in the wrong sector jump back before farming. HERE takes the
        // sector the ship is in right now, so the number never has to be read out of the log.
        _numTargetSector = new NumberField(0, 1_000_000, 1, (int)_cfg.Tuning.TargetSectorId, "");
        _numTargetSector.ValueChanged += (_, _) =>
            _bot.T.TargetSectorId = _cfg.Tuning.TargetSectorId = (uint)_numTargetSector.Value;

        _btnSectorHere.Text = "Here";
        _btnSectorHere.Width = 52;
        _btnSectorHere.Margin = Padding.Empty;
        _btnSectorHere.Click += (_, _) =>
        {
            if (_world.CurrentSectorId == 0)
                AppendLog("The current sector isn't known yet — it is announced on a dock, "
                        + "jump or respawn. Do one of those, then press Here again.");
            else
                _numTargetSector.Value = (int)_world.CurrentSectorId;
        };

        // 0 reads it from the hull card. Only used to pick how much bigger than its own gun
        // spread the hull is assumed to be — a line ship is mostly hull with a few guns on it.
        _numShipClass = new NumberField(0, 4, 1, _cfg.Tuning.ShipTierOverride, "");
        _numShipClass.ValueChanged += (_, _) =>
            _bot.T.ShipTierOverride = _cfg.Tuning.ShipTierOverride = (byte)_numShipClass.Value;

        // 0 means "work it out" — the automatic sources are all guesses of one kind or another,
        // so typing the real number in is the only way to be sure it flies at full speed.
        // These are the two numbers you read off the ship: cruise, and boost.
        _numSpeed = new NumberField(0, 2000, 5, (int)_cfg.Tuning.TopSpeedOverride, "u/s");
        _numSpeed.ValueChanged += (_, _) =>
            _bot.T.TopSpeedOverride = _cfg.Tuning.TopSpeedOverride = _numSpeed.Value;

        _numBoost = new NumberField(0, 2000, 5, (int)_cfg.Tuning.BoostSpeedOverride, "u/s");
        _numBoost.ValueChanged += (_, _) =>
            _bot.T.BoostSpeedOverride = _cfg.Tuning.BoostSpeedOverride = _numBoost.Value;


        // Guessed, never published — the server states no reach for an emplacement, so this is
        // the one number you genuinely have to tune by being shot at. Live, not via bot.json.
        _numKeepOut = new NumberField(0, 20000, 100, (int)_cfg.Tuning.HostileStationKeepOut, "u");
        _numKeepOut.ValueChanged += (_, _) =>
            _bot.T.HostileStationKeepOut = _cfg.Tuning.HostileStationKeepOut = _numKeepOut.Value;

        // How far the ship will range for a richer rock. Squared falloff, so this is the distance
        // at which a rock counts for half its ore — small changes here move the behaviour a lot.
        _numTravel = new NumberField(100, 20000, 50, (int)_cfg.Tuning.RockTravelPenalty, "u");
        _numTravel.ValueChanged += (_, _) =>
            _bot.T.RockTravelPenalty = _cfg.Tuning.RockTravelPenalty = _numTravel.Value;

        // The chip row must fit ALL of them, or the last is silently clipped off the bottom.
        // Three resources at two per rail-width is two rows of 30, with room for a fourth.
        // Caption, chip block, then one row per label/field pair. Twelve pairs now.
        int[] heights = [18, 62, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30];
        _tuningHeight = CardHeight(heights);

        card.Controls.Add(Rows(2, heights,
            (RailCaption("MINE  (click in priority order)"), 2),
            (BuildResourceChips(), 2),
            (RailCaption("RETREAT AT HULL"), 1), (_numRetreat, 1),
            // Named for what they measure. "Stay within" read as a leash and is nothing of the
            // kind — it is the distance at which a rock is worth half its ore, i.e. how far the
            // ship will travel for a better one. "Hold off rock" read as a gap and was measured
            // from the rock's centre, which is why typing 50 into it did nothing.
            (RailCaption("TRAVEL FOR ORE"), 1), (_numTravel, 1),
            (RailCaption("GAP TO ROCK"), 1), (_numRock, 1),
            (RailCaption("SHIP CLASS  1-4"), 1), (_numShipClass, 1),
            (RailCaption("SHIP HALF-SIZE"), 1), (_numHull, 1),
            (RailCaption("KEEP OFF GUNS"), 1), (_numKeepOut, 1),
            (RailCaption("KEEP OFF NPCS"), 1), (_numNpcKeepOut, 1),
            (RailCaption("LOCAL TRAVEL"), 1), (_numLocalTravel, 1),
            (RailCaption("TARGET SECTOR"), 1), (TargetSectorCell(), 1),
            (RailCaption("CRUISE SPEED"), 1), (_numSpeed, 1),
            (RailCaption("BOOST SPEED"), 1), (_numBoost, 1),
            (RailCaption("FALLBACK REACH"), 1), (_numRange, 1)));
        return card;
    }

    private void SetMode(FarmMode mode)
    {
        if (_suppressProfileEvents) return;
        _suppressProfileEvents = true;
        try
        {
            _bot.T.Mode = mode;
            _chipCombat.Checked = mode == FarmMode.Combat;
            _chipMining.Checked = mode == FarmMode.Mining;
        }
        finally { _suppressProfileEvents = false; }
    }

    /// <summary>
    /// Three columns: controls left, map and log in the middle, read-outs right. The controls
    /// used to be two full-width bars above all of this, and eleven chips in a row is what made
    /// the window unusably wide — vertically there was always space going spare.
    /// </summary>
    private Control BuildContent()
    {
        var split = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Bg,
            ColumnCount = 3,
            RowCount = 1,
            Padding = new Padding(0),
        };
        // The left rail scrolls now, and its scrollbar comes out of this column's width. Without
        // paying for it here the cards lose ~17px, the widest chip pair stops fitting two to a
        // row, and the fixed card height clips whatever chip is last — which was Fetch cards.
        split.ColumnStyles.Add(new ColumnStyle(
            SizeType.Absolute, 296 + SystemInformation.VerticalScrollBarWidth));  // controls
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));   // map + log
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 330));  // read-outs

        split.Controls.Add(BuildLeftRail(), 0, 0);
        split.Controls.Add(BuildCentre(), 1, 0);
        split.Controls.Add(BuildRail(), 2, 0);
        return split;
    }

    /// <summary>
    /// The middle column: one of three views of the same sector, with the log underneath.
    ///
    /// They are tabs rather than panes because each wants the whole width — a contacts table
    /// squeezed in beside the map would be exactly the unreadable thing it exists to replace.
    /// </summary>
    private Control BuildCentre()
    {
        var centre = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Bg,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(0),
        };
        centre.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));   // tabs
        centre.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // the view
        centre.RowStyles.Add(new RowStyle(SizeType.Absolute, 158));  // log

        // The map gets the same card treatment as the rails, so the parts read as one app.
        var mapHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(7, 4, 7, 5) };
        mapHost.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(6.5f, 3.5f, mapHost.Width - 14f, mapHost.Height - 9f);
            Theme.DrawRounded(e.Graphics, r, 8f, Theme.Border);
        };
        mapHost.Controls.Add(_map);

        var listHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(7, 4, 7, 5) };
        listHost.Controls.Add(_contacts);

        var kitHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(7, 4, 7, 5) };
        kitHost.Controls.Add(_loadout);

        var diagHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(7, 4, 7, 5) };
        diagHost.Controls.Add(_diagView);

        var sessionsHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(7, 4, 7, 5) };
        sessionsHost.Controls.Add(_sessionsView);

        _viewHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
        _viewHost.Controls.AddRange([listHost, kitHost, mapHost, diagHost, sessionsHost]);

        var logHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(7, 0, 7, 10) };
        _log.Dock = DockStyle.Fill;
        logHost.Controls.Add(_log);

        centre.Controls.Add(BuildTabs(mapHost, listHost, kitHost, diagHost, sessionsHost), 0, 0);
        centre.Controls.Add(_viewHost, 0, 1);
        centre.Controls.Add(logHost, 0, 2);

        // Again once the window exists. Re-parenting a control can undo a Visible set before
        // its handle was made, and a centre column showing two views at once is worse than
        // paying for one redundant call.
        Load += (_, _) => Show(_cfg.SelectedView);
        return centre;
    }

    private Control BuildTabs(Control map, Control list, Control loadout,
                              Control diagnostics, Control sessions)
    {
        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = Theme.Bg,
            Padding = new Padding(14, 5, 8, 0), WrapContents = false,
        };

        void Tab(string title, Control view)
        {
            var chip = new ToggleChip(title) { Margin = new Padding(0, 0, 5, 0) };
            chip.CheckedChanged += (_, _) =>
            {
                if (_switchingView) return;              // Show() is turning the others off
                if (chip.Checked) { Show(title); return; }

                // Clicking the tab you are already on cannot leave you on no tab at all.
                _switchingView = true;
                chip.Checked = true;
                _switchingView = false;
            };
            _views.Add((title, chip, view));
            bar.Controls.Add(chip);
        }

        Tab("Map", map);
        Tab("Contacts", list);
        Tab("Loadout", loadout);
        Tab("Diagnostics", diagnostics);
        Tab("Sessions", sessions);
        Show(_cfg.SelectedView);
        return bar;
    }

    /// <summary>Only one view at a time, and the chips behave as one radio group.</summary>
    private void Show(string title)
    {
        // An unknown name in bot.json falls back to the map rather than leaving a blank panel.
        if (_views.All(v => v.Title != title)) title = "Map";

        _switchingView = true;
        try
        {
            foreach (var (name, chip, view) in _views)
            {
                bool on = name == title;
                view.Visible = on;
                chip.Checked = on;
                if (on) view.BringToFront();
            }
        }
        finally { _switchingView = false; }

        _cfg.SelectedView = title;
    }

    /// <summary>
    /// Keep the game client's own Unity log the moment the client ends a session.
    ///
    /// Unity rewrites <c>output_log.txt</c> on every launch, so the record of why a client died
    /// is destroyed by the very relaunch that follows it — which is how three overnight crashes
    /// in a row left nothing to read. The copy costs nothing and turns the next "wtf crashed
    /// the client" into an open file.
    /// </summary>
    private void SnapshotClientLog()
    {
        try
        {
            var dir = _cfg.CurrentClient?.Path;
            if (string.IsNullOrEmpty(dir)) return;
            var src = Path.Combine(dir, "bsgo_Data", "output_log.txt");
            if (!File.Exists(src)) return;

            var logs = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(logs);
            var dst = Path.Combine(logs, $"client-exit-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            File.Copy(src, dst, overwrite: true);
            AppendLog($"The client ended the session — kept its Unity log as logs\\{Path.GetFileName(dst)}.");
        }
        catch (Exception ex)
        {
            AppendLog($"Could not keep the client's Unity log: {ex.Message}");
        }
    }

    private Control BuildLeftRail()
    {
        // The rail is taller than any window now — three cards of controls — so it lives inside
        // a scroll host. NOT TableLayoutPanel.AutoScroll: that combination quietly failed to
        // produce a scrollbar, which is how TARGET SECTOR shipped invisible below the window
        // edge on a 1440p screen. A plain Panel scroll host with a Top-docked, self-sized table
        // is the WinForms arrangement that actually scrolls.
        var host = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Bg,
            AutoScroll = true,
        };

        var rail = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Theme.Bg,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(12, 10, 6, 10),
        };
        // Build first, size second. Each card works out the height it needs from the very array
        // its grid is built from and reports it, so the rail cannot disagree with its contents.
        // Hand-written constants here clipped the Go farm button off the bottom for as long as
        // anyone can tell, and then did the same to the ship picker.
        var connection = BuildConnectionCard();
        var control = BuildControlCard();
        var tuning = BuildTuningCard();

        rail.RowStyles.Add(new RowStyle(SizeType.Absolute, _connectionHeight));
        rail.RowStyles.Add(new RowStyle(SizeType.Absolute, _controlHeight));
        rail.RowStyles.Add(new RowStyle(SizeType.Absolute, _tuningHeight));

        rail.Controls.Add(connection, 0, 0);
        rail.Controls.Add(control, 0, 1);
        rail.Controls.Add(tuning, 0, 2);

        host.Controls.Add(rail);
        return host;
    }

    private Control BuildRail()
    {
        var rail = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Bg,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(6, 10, 12, 10),
        };
        rail.RowStyles.Add(new RowStyle(SizeType.Absolute, 140));   // link
        rail.RowStyles.Add(new RowStyle(SizeType.Absolute, 148));   // sector
        rail.RowStyles.Add(new RowStyle(SizeType.Absolute, 132));   // hunt
        rail.RowStyles.Add(new RowStyle(SizeType.Percent, 100));    // diagnostics

        var linkCard = new Card("Link") { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 8) };
        _link.Dock = DockStyle.Fill;
        linkCard.Controls.Add(_link);

        var sectorCard = new Card("Sector") { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 8) };
        _sector.Dock = DockStyle.Fill;
        sectorCard.Controls.Add(_sector);

        var huntCard = new Card("Hunt") { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 8) };
        huntCard.Note = "none = any hostile";
        var huntFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = Theme.Card, WrapContents = true, AutoScroll = false,
        };
        foreach (var t in PreyChoices)
        {
            var chip = new ToggleChip(t.ToString(), _cfg.Tuning.Prey.Contains(t))
            {
                Tag2 = t,
                Margin = new Padding(0, 0, 5, 5),
            };
            chip.CheckedChanged += (_, _) => ApplyPrey();
            _preyChips.Add(chip);
            huntFlow.Controls.Add(chip);
        }
        huntCard.Controls.Add(huntFlow);

        // The full diagnostics moved to their own tab, where they have the width to be read.
        // What stays on the rail is the one thing worth glancing at constantly: the session.
        var sessionCard = new Card("Session") { Dock = DockStyle.Fill };
        _session.Dock = DockStyle.Fill;
        sessionCard.Controls.Add(_session);

        // Zeroing the meter is what makes it an experiment rather than a running total: refit,
        // reset, mine for a while, compare ore/hour against the fit you had before.
        var btnReset = new FlatButton { Text = "Reset meter", Dock = DockStyle.Bottom, Height = 26 };
        btnReset.Click += (_, _) =>
        {
            _bot.Meter.Reset();
            AppendLog("Mining meter reset — regen, ore/hour and the time split start again.");
        };
        sessionCard.Controls.Add(btnReset);
        btnReset.BringToFront();

        rail.Controls.Add(linkCard, 0, 0);
        rail.Controls.Add(sectorCard, 0, 1);
        rail.Controls.Add(huntCard, 0, 2);
        rail.Controls.Add(sessionCard, 0, 3);
        return rail;
    }

    private static Label RailCaption(string text) => new()
    {
        Text = text,
        AutoSize = false,
        Dock = DockStyle.Fill,
        Font = Theme.Header,
        ForeColor = Theme.Faint,
        TextAlign = ContentAlignment.MiddleLeft,
        Margin = Padding.Empty,
    };

    /// <summary>
    /// Lays controls into a fixed grid inside a card. Each entry carries how many columns it
    /// spans, so a full-width button and a pair of half-width ones share one description.
    /// </summary>
    /// <summary>Bottom margin <see cref="Rows"/> puts under every cell.</summary>
    private const int RowGap = 4;

    /// <summary>Vertical chrome a <see cref="Card"/> adds around its content: its own top and
    /// bottom padding, plus the 8px margin each card carries below itself in the rail.</summary>
    private const int CardChrome = 30 + 10 + 8;

    /// <summary>
    /// How tall a rail row has to be for a <see cref="Rows"/> grid inside a card to fit.
    ///
    /// The rail sizes its rows absolutely — it does not grow to fit what it holds, it clips. That
    /// has cost the Go farm button, the ship picker and the hull-size field one at a time, each
    /// time by someone adding a row and not knowing there was a second number to change. So the
    /// number is computed from the same array the grid is built from, and there is no longer a
    /// second number to forget.
    /// </summary>
    private static int CardHeight(int[] heights) =>
        heights.Sum() + heights.Length * RowGap + CardChrome;

    /// <summary>The target-sector field with its HERE button beside it, sharing one grid cell.
    /// Fill must sit at z-index 0 so the docked button takes its width first.</summary>
    private Panel TargetSectorCell()
    {
        var cell = new Panel { BackColor = Theme.Card, Margin = Padding.Empty };
        _numTargetSector.Dock = DockStyle.Fill;
        _btnSectorHere.Dock = DockStyle.Right;
        cell.Controls.Add(_numTargetSector);
        cell.Controls.Add(_btnSectorHere);
        return cell;
    }

    private static TableLayoutPanel Rows(int columns, int[] heights,
                                         params (Control Control, int Span)[] cells)
    {
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Card,
            ColumnCount = columns,
            RowCount = heights.Length,
            Margin = Padding.Empty,
        };
        for (int i = 0; i < columns; i++)
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / columns));
        foreach (int h in heights)
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, h));

        int row = 0, col = 0;
        foreach (var (control, span) in cells)
        {
            if (col >= columns) { col = 0; row++; }
            control.Dock = DockStyle.Fill;
            control.Margin = new Padding(col == 0 ? 0 : 3, 0, 0, 4);
            grid.Controls.Add(control, col, row);
            if (span > 1) grid.SetColumnSpan(control, span);
            col += span;
        }
        return grid;
    }

    // ---------------------------------------------------------------- behaviour

    /// <summary>Tells you up front whether the real server is actually up, before you launch anything.</summary>
    private async Task CheckServerAsync()
    {
        var s = _cfg.CurrentServer;
        if (s is null) { _serverReachable = "none"; return; }

        var (host, port) = (s.Host, s.Port);
        try
        {
            using var probe = new System.Net.Sockets.TcpClient();
            var connect = probe.ConnectAsync(host, port);
            if (await Task.WhenAny(connect, Task.Delay(2000)) == connect && probe.Connected)
            {
                _serverReachable = "up";
                AppendLog($"Game server reachable at {host}:{port}.");
                return;
            }
        }
        catch { /* fall through */ }

        _serverReachable = "down";
        AppendLog($"WARNING: no server on {host}:{port}. Start it first.");
    }

    private void LaunchGame()
    {
        var client = _cfg.CurrentClient;
        var server = _cfg.CurrentServer;

        if (client is null || server is null)
        {
            MessageBox.Show("Pick a server and a client first (Profiles).");
            return;
        }

        var exe = Path.Combine(client.Path, "bsgo.exe");
        if (!File.Exists(exe))
        {
            MessageBox.Show($"bsgo.exe not found at:\n{exe}\n\nFix the path under Profiles.",
                "Client not found", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        // +gameServer points at the proxy, never at the real server. +cdn must end with
        // a slash and point at the client folder so assets load locally.
        var cdn = client.Path.TrimEnd('\\') + "/";
        var args = string.Join(' ',
            "+projectID", "547",
            "+userID", server.PlayerId,
            "+sessionID", "c7faac2379e35f6404eced5f484210ba",
            "+trackingID", "6cc3a6e78a753f29ccabaa0f79b7041b",
            "+gameServer", _cfg.ListenHost,
            "+cdn", cdn,
            "+language", server.Language,
            "+session", server.Session,
            "+version", client.Version);

        Process.Start(new ProcessStartInfo(exe, args) { WorkingDirectory = client.Path });
        AppendLog($"Launched '{client.Name}' -> proxy {_cfg.ListenHost}:{_cfg.ListenPort} -> '{server.Name}'");
    }

    // ---------------------------------------------------------------- second instance

    /// <summary>
    /// Opens another bot window on its own config file, so both live servers farm at once.
    ///
    /// One process per server rather than one process juggling two sessions: every panel, the
    /// world state and the farm loop assume a single ship, and two windows keep that assumption
    /// true. Each window owns its own loopback address — the client hardcodes port 27050 but
    /// takes any IP — and its own config file, found here as the other <c>bot*.json</c> next to
    /// the exe. No second config yet means this is the first time: one is cloned from the
    /// current config on the next free loopback address, ready except for picking its server.
    /// </summary>
    private void LaunchSecondInstance()
    {
        var mine = Path.GetFullPath(Config.FilePath);
        var others = Directory.GetFiles(AppContext.BaseDirectory, "bot*.json")
            .Where(f => !string.Equals(Path.GetFullPath(f), mine, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (others.Count == 0)
        {
            try { others.Add(CreateSiblingConfig()); }
            catch (Exception ex) { AppendLog("Could not create a second config: " + ex.Message); return; }
        }

        if (others.Count == 1)
        {
            SpawnInstance(others[0]);
            return;
        }

        var menu = new ContextMenuStrip();
        foreach (var f in others)
        {
            var file = f;
            menu.Items.Add(Path.GetFileName(file), null, (_, _) => SpawnInstance(file));
        }
        menu.Show(_btnSecond, new Point(0, _btnSecond.Height));
    }

    private void SpawnInstance(string configPath)
    {
        var name = Path.GetFileName(configPath);
        Process.Start(new ProcessStartInfo(Application.ExecutablePath, $"--config \"{name}\"")
        {
            WorkingDirectory = AppContext.BaseDirectory,
        });
        AppendLog($"Started a second bot on {name}. If its proxy fails to start, "
                + "that config is already running (or shares this window's listen address).");
    }

    /// <summary>A copy of this config on the next loopback address. Servers, clients and tuning
    /// come along; only the listen address differs, so the profiles never have to be set up
    /// twice.</summary>
    private string CreateSiblingConfig()
    {
        string path;
        int n = 2;
        while (File.Exists(path = Path.Combine(AppContext.BaseDirectory, $"bot{n}.json"))) n++;

        var copy = JsonSerializer.Deserialize<Config>(JsonSerializer.Serialize(_cfg))!;
        copy.ListenHost = NextLoopback(_cfg.ListenHost);
        File.WriteAllText(path,
            JsonSerializer.Serialize(copy, new JsonSerializerOptions { WriteIndented = true }));

        AppendLog($"Created {Path.GetFileName(path)} listening on {copy.ListenHost} — "
                + "pick the other server in the new window.");
        return path;
    }

    private static string NextLoopback(string host)
    {
        var parts = host.Split('.');
        return parts.Length == 4 && parts[0] == "127" && int.TryParse(parts[3], out var n)
            ? $"{parts[0]}.{parts[1]}.{parts[2]}.{Math.Min(n + 1, 254)}"
            : "127.0.0.2";
    }

    /// <summary>Repopulates both dropdowns from config without firing selection side effects.</summary>
    private void RefreshProfileBoxes()
    {
        _suppressProfileEvents = true;
        try
        {
            _serverBox.Items.Clear();
            foreach (var s in _cfg.Servers) _serverBox.Items.Add(s);
            if (_cfg.Servers.Count > 0)
                _serverBox.SelectedIndex = Math.Clamp(_cfg.SelectedServer, 0, _cfg.Servers.Count - 1);

            _clientBox.Items.Clear();
            foreach (var c in _cfg.Clients) _clientBox.Items.Add(c);
            if (_cfg.Clients.Count > 0)
                _clientBox.SelectedIndex = Math.Clamp(_cfg.SelectedClient, 0, _cfg.Clients.Count - 1);

            _shipBox.Items.Clear();
            if (_cfg.CurrentServer is { } srv)
            {
                // Touch CurrentShip first: it is what guarantees the list is never empty, and an
                // empty ship box would be a dropdown you cannot select your way out of.
                _ = srv.CurrentShip;
                foreach (var ship in srv.Ships) _shipBox.Items.Add(ship);
                _shipBox.SelectedIndex = Math.Clamp(srv.SelectedShip, 0, srv.Ships.Count - 1);
            }
        }
        finally
        {
            _suppressProfileEvents = false;
        }
    }

    /// <summary>
    /// Switches to another of your ships: its tuning, its slots, its learned ability ids.
    ///
    /// Deliberately manual. The server does say which ship is active, but acting on that would
    /// mean a wrong guess silently flying the Raptor's collision margins on a Vanir, and the
    /// failure would look like bad piloting rather than a wrong profile.
    /// </summary>
    private void OnShipChanged()
    {
        if (_suppressProfileEvents || _shipBox.SelectedIndex < 0) return;
        if (_cfg.CurrentServer is not { } srv) return;
        if (_shipBox.SelectedIndex == srv.SelectedShip) return;

        SaveWeapons();                       // against the ship we are leaving
        srv.SelectedShip = _shipBox.SelectedIndex;

        var ship = srv.CurrentShip;

        // Snapshot first. Setting a chip or a number below raises its Changed event, and those
        // handlers write straight into the live tuning — which is now this ship's. Without this,
        // loading the controls would stamp the OUTGOING ship's values onto the incoming one, and
        // the corruption would look exactly like the profile never having been saved.
        var truth = Config.CloneTuning(ship.Tuning);
        LoadControlsFromTuning(ship.Tuning);
        ship.Tuning = truth;

        _bot.T = ship.Tuning;
        _bot.Weapons.Clear();
        AdoptServerIdentity();               // restores this ship's weapons, hexes and loadout
        _loadout.Reload();
        foreach (var chip in _resourceChips) RankChip(chip);
        _cfg.Save();

        AppendLog($"Flying \"{ship.Name}\" — its own tuning, slots and learned abilities. "
                + $"Gap to rock {ship.Tuning.AsteroidStandoff:F0}u, "
                + $"clip-through under {ship.Tuning.IgnoreCollisionHullFraction:P0} of hull.");
    }

    /// <summary>
    /// Pushes a tuning's values back out into every control that shows one.
    ///
    /// The counterpart to the handlers, which only ever run the other way. Nothing called this
    /// before because there was one tuning for the life of the window; switching ships is the
    /// first thing that can change it underneath the UI.
    /// </summary>
    private void LoadControlsFromTuning(BotTuning t)
    {
        _chipCombat.Checked = t.Mode == FarmMode.Combat;
        _chipMining.Checked = t.Mode == FarmMode.Mining;

        _chipApproach.Checked = t.AutoApproach;
        _chipBoost.Checked = t.UseBoost;
        _chipLoot.Checked = t.AutoLoot;
        _chipPlayers.Checked = t.AttackPlayers;
        _chipGunsOnRocks.Checked = t.FireGunsWhileMining;
        _chipDefend.Checked = t.DefendSelf;
        _chipOptimal.Checked = t.HoldFireUntilOptimal;
        _chipAvoidStations.Checked = t.AvoidHostileStations;
        _chipRepair.Checked = t.UseRepairAbility;
        _chipAutoUndock.Checked = t.AutoUndock;
        _chipHangarRepair.Checked = t.AutoRepair;
        _chipAvoidRocks.Checked = t.AvoidCollisions;
        _chipCatalogue.Checked = t.FetchCatalogue;
        _chipAvoidPlayers.Checked = t.AvoidPlayers;

        _numRange.Value = (int)t.FallbackRange;
        _numRetreat.Value = (int)(t.RetreatHull * 100f);
        _numRock.Value = (int)t.AsteroidStandoff;
        _numHull.Value = (int)t.HullRadius;
        _numShipClass.Value = t.ShipTierOverride;
        _numNpcKeepOut.Value = (int)t.HostileShipKeepOut;
        _numLocalTravel.Value = (int)t.LocalTravelSeconds;
        _numTargetSector.Value = (int)t.TargetSectorId;
        _numSpeed.Value = (int)t.TopSpeedOverride;
        _numBoost.Value = (int)t.BoostSpeedOverride;
        _numKeepOut.Value = (int)t.HostileStationKeepOut;
        _numTravel.Value = (int)t.RockTravelPenalty;

        foreach (var chip in _preyChips)
            if (chip.Tag2 is SpaceEntityType prey) chip.Checked = t.Prey.Contains(prey);

        foreach (var chip in _resourceChips)
            if (chip.Tag2 is ResourceType res) chip.Checked = t.WantedResources.Contains(res);
    }

    /// <summary>Adds a ship, starting from the current one's tuning rather than from defaults —
    /// most of what you learned about flying one hull still applies to the next.</summary>
    private void AddShip()
    {
        if (_cfg.CurrentServer is not { } srv) return;

        string? name = Widgets.Prompt(this, "New ship", "Name this ship (e.g. Advanced Vanir)");
        if (string.IsNullOrWhiteSpace(name)) return;

        SaveWeapons();
        var ship = _cfg.DuplicateCurrentShip(name.Trim());
        srv.SelectedShip = srv.Ships.IndexOf(ship);

        _bot.T = ship.Tuning;
        _bot.Weapons.Clear();
        _loadout.Reload();
        RefreshProfileBoxes();
        _cfg.Save();

        AppendLog($"Added \"{ship.Name}\", copying the previous ship's tuning. Its slots and "
                + "ability ids start empty on purpose — slot 4 on one hull is not slot 4 on "
                + "another. Fire each weapon once, or let the catalogue fill them in.");
    }

    private void OnServerChanged()
    {
        if (_suppressProfileEvents || _serverBox.SelectedIndex < 0) return;

        SaveWeapons();                       // against the profile we're leaving
        _cfg.SelectedServer = _serverBox.SelectedIndex;
        var s = _cfg.CurrentServer;
        if (s is null) return;

        _proxy.SetUpstream(s.Host, s.Port);
        _world.Clear();
        _bot.Weapons.Clear();

        // Ships belong to a server, so the list and the tuning both change underneath us. Same
        // snapshot-and-restore as OnShipChanged, for the same reason: loading the controls fires
        // their handlers, and those write into whatever tuning is live by then.
        var ship = s.CurrentShip;
        var truth = Config.CloneTuning(ship.Tuning);
        LoadControlsFromTuning(ship.Tuning);
        ship.Tuning = truth;
        _bot.T = ship.Tuning;

        AdoptServerIdentity();
        _loadout.Reload();
        RefreshProfileBoxes();
        foreach (var chip in _resourceChips) RankChip(chip);

        _serverReachable = "checking";
        _ = CheckServerAsync();

        if (_proxy.ClientConnected)
            AppendLog("Server switched. Restart the game client for it to take effect.");
    }

    private void EditProfiles()
    {
        using var dlg = new ProfilesDialog(_cfg);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        SaveWeapons();
        _cfg.Save();
        RefreshProfileBoxes();
        OnServerChanged();
        AppendLog("Profiles saved.");
    }

    // ---------------------------------------------------------------- catching a live session

    /// <summary>
    /// Arms the watcher that lifts a session off the real launcher's client.
    ///
    /// The launcher hands its own client a one-shot token. The bot needs it — and the server,
    /// player id and client version that go with it — to relaunch through the proxy, and none of
    /// those are on the wire: the launcher's client connects straight to the live server, so the
    /// proxy never sees that session at all.
    /// </summary>
    private void ToggleCatcher()
    {
        if (_catcher.Running)
        {
            _catcher.Stop();
            _btnCatch.Text = "Catch session";
            _btnCatch.Tint = Color.Empty;
            return;
        }

        _catcher.Start();
        _btnCatch.Text = "Watching — log in";
        _btnCatch.Tint = Theme.Good;
        _btnCatch.Invalidate();
    }

    /// <summary>
    /// Files a captured session into the profile for that server and selects it.
    ///
    /// Profiles are matched by host, not by name: there is more than one live server now, and a
    /// single shared "captured" slot meant a login on one server silently erased the other's
    /// profile. A host that has no profile yet gets a new one. Sessions still overwrite in place —
    /// a session is only good once, so a list of expired ones is a list of things that no longer
    /// work. Your own profiles are left alone.
    /// </summary>
    private void AdoptCapturedSession(CapturedSession s)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { try { BeginInvoke(() => AdoptCapturedSession(s)); } catch { } return; }

        var profile = _cfg.Servers.FirstOrDefault(p => p.Host == s.Host);
        if (profile is null)
        {
            profile = new ServerProfile { Name = $"Live {s.Host} (captured)" };
            _cfg.Servers.Add(profile);
        }

        profile.Host = s.Host;
        // The client hardcodes 27050, so that is the port the live server answers on. The proxy
        // owns 27050 locally and forwards here — different interfaces, no clash.
        profile.Port = _cfg.ListenPort;
        profile.PlayerId = s.PlayerId;
        profile.Session = s.Session;
        profile.Language = s.Language.Length > 0 ? s.Language : "en";

        // The version has to match what the launcher used or the live server refuses the client.
        // It belongs to the install the launcher ran, so only clients under that path are
        // updated — the other server's client may be on a different build.
        if (s.Version.Length > 0)
            foreach (var c in _cfg.Clients)
                if (s.ExePath.Length == 0
                    || s.ExePath.StartsWith(c.Path, StringComparison.OrdinalIgnoreCase))
                    c.Version = s.Version;

        _cfg.SelectedServer = _cfg.Servers.IndexOf(profile);
        _cfg.Save();

        RefreshProfileBoxes();
        OnServerChanged();

        AppendLog($"Session filed as \"{profile.Name}\" — player {s.PlayerId} on {s.Host}. "
                + "Click Launch game to spend it through the proxy.");
    }

    private void ToggleProxy()
    {
        if (!_proxyRunning)
        {
            try
            {
                _proxy.Start(_cfg.ListenHost, _cfg.ListenPort);
                _proxyRunning = true;
                _btnProxy.Text = "Stop proxy";
                _btnFarm.Enabled = true;
                AppendLog($"Now launch the client with +gameServer {_cfg.ListenHost}");
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Could not listen on {_cfg.ListenHost}:{_cfg.ListenPort}.\n\n{ex.Message}\n\n" +
                    "Either another bsgobot is already running on this address, or the game " +
                    "server is still bound to 27050. The client hardcodes the port but not the " +
                    "IP, so a second instance must use its own loopback address — run it with " +
                    "--config <file> and set ListenHost to 127.0.0.2 in that file.",
                    "Proxy failed to start", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        else
        {
            _proxy.Stop();
            _proxyRunning = false;
            _btnProxy.Text = "Start proxy";
            _btnFarm.Enabled = false;
            if (_bot.Enabled) ToggleFarm();
        }
    }

    private void ToggleFarm()
    {
        if (_bot.Enabled)
        {
            _bot.Stop();
            _btnFarm.Text = "Go farm";
        }
        else
        {
            _bot.Start();
            _btnFarm.Text = "Stop farm";
        }
    }

    /// <summary>Dock is a run, not an instant action — the same button aborts it.</summary>
    private void ToggleDock()
    {
        if (_bot.IsDocking) _bot.CancelDock();
        else _bot.BeginDock();
        SyncDockButton();
    }

    private void SyncDockButton()
    {
        string want = _bot.IsDocking ? "Cancel dock" : "Dock";
        if (_btnDock.Text != want) _btnDock.Text = want;
        if (_btnDock.Width != (_bot.IsDocking ? 104 : 76)) _btnDock.Width = _bot.IsDocking ? 104 : 76;

        // Starting a dock run stops the farm from underneath the farm button — keep its label
        // honest rather than leaving it claiming "Stop farm" when nothing is farming.
        string farm = _bot.Enabled ? "Stop farm" : "Go farm";
        if (_btnFarm.Text != farm) _btnFarm.Text = farm;
    }

    // ---------------------------------------------------------------- refresh

    private double _uiWorstMs;
    private DateTime _uiReportedAt = DateTime.UtcNow;

    /// <summary>
    /// Times the UI refresh and reports an overrun, at most once every 10 seconds.
    ///
    /// This handler runs on the message pump. Every millisecond it spends is a millisecond the
    /// window is not responding to being dragged, clicked or raised — so when the app "feels
    /// frozen", this is the number that says whether the cause is here or elsewhere.
    /// </summary>
    private void RefreshUiTimed()
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try { RefreshUi(); }
        finally
        {
            double ms = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (ms > _uiWorstMs) _uiWorstMs = ms;

            var now = DateTime.UtcNow;
            if ((now - _uiReportedAt).TotalSeconds >= 10)
            {
                if (_uiWorstMs > 80)
                    AppendLog($"UI refresh is slow — {_uiWorstMs:F0}ms worst in the last 10s, "
                            + "against a 250ms tick. The window will feel stuck.");
                _uiReportedAt = now;
                _uiWorstMs = 0;
            }
        }
    }

    private void RefreshUi()
    {
        SyncDockButton();
        var objs = _world.Snapshot();
        int hostiles = objs.Count(o => EntityTypes.IsNpcCombatant(o.Id));
        int rocks = objs.Count(o => EntityTypes.IsMinable(o.Id));
        int loot = objs.Count(o => EntityTypes.IsLootable(o.Id));
        int located = objs.Count(o => o.HasPosition);

        // How much of the sector your own client is filtering out locally.
        var detection = _world.Detection;
        int dark = detection.Known
            ? objs.Count(o => !o.IsMe && o.HasPosition && _world.LayerOf(o, detection) == ContactLayer.Dark)
            : 0;

        var reach = _serverReachable switch
        {
            "up" => Theme.Good,
            "down" => Theme.Bad,
            _ => Theme.Warn,
        };

        _link.SetRows([
            new("server", _cfg.CurrentServer?.Name ?? "none"),
            new("upstream", $"{_cfg.CurrentServer?.Host}:{_cfg.CurrentServer?.Port}", Theme.Muted),
            new("reachable", _serverReachable, reach),
            new("proxy", _proxyRunning ? $"{_cfg.ListenPort}" : "stopped",
                _proxyRunning ? Theme.Good : Theme.Faint),
            new("game", _proxy.ClientConnected ? "connected" : "waiting",
                _proxy.ClientConnected ? Theme.Good : Theme.Faint),
            new("", "", null, Spacer: true),
            new("msgs in / out", $"{_proxy.MessagesFromServer:N0} / {_proxy.MessagesFromClient:N0}"),
            new("injected", $"{_proxy.FramesInjected:N0}", Theme.Accent),
        ]);

        _sector.SetRows([
            // "?" until a scene change names it — the server only states the sector on a dock,
            // jump or respawn. The green/plain split is against the farm's target, if one is set.
            new("sector", _world.CurrentSectorId == 0 ? "?" : $"{_world.CurrentSectorId}",
                _world.CurrentSectorId == 0 ? Theme.Faint
                : _bot.T.TargetSectorId == 0 ? Theme.Text
                : _world.CurrentSectorId == _bot.T.TargetSectorId ? Theme.Good : Theme.Warn),
            new("objects", $"{objs.Count:N0}"),
            new("located", $"{located:N0}", located == 0 ? Theme.Faint : Theme.Text),
            new("hostiles", $"{hostiles:N0}", hostiles > 0 ? Theme.Bad : Theme.Faint),
            new("asteroids", $"{rocks:N0}", rocks > 0 ? Theme.Warn : Theme.Faint),
            new("loot / cargo", $"{loot:N0}", loot > 0 ? Theme.Good : Theme.Faint),
            new("client-dark", detection.Known ? $"{dark:N0}" : "n/a",
                detection.Known && dark > 0 ? Theme.Warn : Theme.Faint),
            new("", "", null, Spacer: true),
            new("kills / shots", $"{_bot.Kills:N0} / {_bot.ShotsFired:N0}"),
            new("loot taken", $"{_bot.LootTaken:N0}", _bot.LootTaken > 0 ? Theme.Good : Theme.Faint),
        ]);

        RefreshSessionCard();
        _header.Invalidate();

        // Only the visible view is refreshed. Building fifty-odd diagnostics rows, or a
        // contacts table nobody is looking at, four times a second, is work for nothing.
        if (_map.Visible) _map.Invalidate();
        if (_contacts.Visible) _contacts.Tick();
        if (_loadout.Visible) _loadout.Tick();
        if (_diagView.Visible) _diagView.SetSections(_bot.DiagnosticSections());
        if (_sessionsView.Visible) _sessionsView.SetSessions(_bot.Sessions.All());
    }

    /// <summary>The rail's at-a-glance answer to "is it earning": the live run, or the last one.</summary>
    private void RefreshSessionCard()
    {
        var now = DateTime.UtcNow;
        var live = _bot.Sessions.Current;
        var shown = live ?? _bot.Sessions.All().FirstOrDefault();

        var rows = new List<StatList.Row>
        {
            new("state", live is not null ? "farming" : "stopped",
                live is not null ? Theme.Good : Theme.Faint),
        };

        if (shown is null)
        {
            rows.Add(new("history", "no runs yet", Theme.Faint));
        }
        else
        {
            rows.Add(new(live is not null ? "started" : "last run",
                shown.StartedUtc.ToLocalTime().ToString(live is not null ? "HH:mm:ss" : "MMM d HH:mm")));
            rows.Add(new("ran for", SessionsView.FmtSpan(shown.Duration(now))));
            rows.Add(new("ore", $"{shown.Mined:N0}", shown.Mined > 0 ? Theme.Text : Theme.Faint));
            rows.Add(new("ore/hour",
                shown.OrePerHour(now) is { } oph ? $"{oph:N0}" : "…",
                Theme.Accent));
            foreach (var (guid, count) in shown.Gained.OrderByDescending(kv => kv.Value).Take(3))
                rows.Add(new(_bot.NameItem(guid).ToLowerInvariant(), $"{count:N0}", Theme.Muted));
            if (shown.Deaths > 0) rows.Add(new("deaths", $"{shown.Deaths}", Theme.Bad));
        }

        int runs = _bot.Sessions.All().Count(s => !s.Running);
        rows.Add(new("", "", null, Spacer: true));
        rows.Add(new("recorded runs", $"{runs}", runs > 0 ? Theme.Text : Theme.Faint));
        rows.Add(new("meter total", $"{_bot.Meter.MinedGained:N0}", Theme.Muted));

        _session.SetRows(rows);
    }

    private void AppendLog(string message)
    {
        // Written once, on the calling thread. The marshalling hop below goes straight to
        // _log.Add rather than back through this method — re-entering it wrote every
        // background-thread message to the file twice.
        WriteLogFile(message);

        if (IsDisposed) return;
        if (InvokeRequired) { try { BeginInvoke(() => _log.Add(message)); } catch { } return; }
        _log.Add(message);
    }

    private static readonly Lock _logFileGate = new();
    private static string? _logFilePath;

    /// <summary>
    /// Appends one line to today's log file, and never throws: a logger that can take the
    /// application down with it is worse than no logger.
    /// </summary>
    private static void WriteLogFile(string message)
    {
        try
        {
            lock (_logFileGate)
            {
                if (_logFilePath is null)
                {
                    var dir = Path.Combine(AppContext.BaseDirectory, "logs");
                    Directory.CreateDirectory(dir);
                    _logFilePath = Path.Combine(dir, $"bot-{DateTime.Now:yyyy-MM-dd}.log");
                }
                File.AppendAllText(_logFilePath,
                    $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
        }
        catch { /* a full or read-only disk is not worth a crash */ }
    }
}
