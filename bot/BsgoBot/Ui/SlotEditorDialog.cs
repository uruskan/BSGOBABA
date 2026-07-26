using BsgoBot.Bot;
using BsgoBot.Protocol;
using BsgoBot.World;

namespace BsgoBot.Ui;

/// <summary>
/// Says what is in one hex.
///
/// The hard part is not the typing, it is the binding: knowing that the hex you are looking at
/// in the game is ability id 7 and not ability id 3. Nothing on the wire states that, so this
/// dialog offers the two things that can settle it —
///
///  * <b>Bind by firing</b>: arm it, press the ability in game, and the id that goes past on the
///    wire is the answer. This is proof, not inference.
///  * <b>Test fire</b>: cast the id you have picked and watch which hex sweeps its cooldown in
///    the real client. The reverse direction, for when you'd rather not lose the ammo.
///
/// Everything else on the form is what you can read off the item's own card.
/// </summary>
public sealed class SlotEditorDialog : Form
{
    private readonly WorldState _world;
    private readonly FarmBot _bot;
    private readonly int _hex;

    private readonly TextField _slotId;
    private readonly TextField _name = new(placeholder: "e.g. Strike Damage Control");
    private readonly DarkCombo _category = new();
    private readonly TextField _level = new(placeholder: "1", numeric: true);
    private readonly DarkCombo _role = new();
    private readonly TextField _max = new(placeholder: "unknown", numeric: true);
    private readonly TextField _optimal = new(placeholder: "unknown", numeric: true);
    private readonly TextField _min = new(placeholder: "unknown", numeric: true);
    private readonly TextField _reload = new(placeholder: "unknown", numeric: true);
    private readonly TextField _power = new(placeholder: "unknown", numeric: true);
    private readonly TextField _ammo = new(placeholder: "e.g. Strike Standard DC Pack");
    private readonly ToggleChip _enabled = new("Bot may fire this", true);
    private readonly Label _wire = new();
    private readonly FlatButton _bind = new();

    private bool _arming;

    /// <summary>Roles you can pick, and what each one makes the bot do with the slot.</summary>
    private static readonly (string Label, WeaponRole? Role)[] Roles =
    [
        ("Auto — let the bot decide", null),
        ("Combat — fire it at enemies", WeaponRole.Combat),
        ("Mining — fire it at asteroids", WeaponRole.Mining),
        ("Scanner — use it to scan rocks", WeaponRole.Scanner),
        ("Repair — cast it on myself when hurt", WeaponRole.Repair),
        ("Utility — never fire it", WeaponRole.Utility),
    ];

    public SavedSlot Result { get; private set; } = new();

    public SlotEditorDialog(WorldState world, FarmBot bot, int hex, SavedSlot? existing,
                            IReadOnlyList<ushort> knownSlots)
    {
        _world = world;
        _bot = bot;
        _hex = hex;

        Text = hex <= LoadoutView.WeaponHexes ? $"Weapon hex {hex}" : $"Ability bar slot {hex - LoadoutView.WeaponHexes}";
        Width = 560;
        Height = 620;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.Ui;
        HandleCreated += (_, _) => Theme.UseDarkTitleBar(this);

        _slotId = new TextField(existing?.SlotId is > 0 ? existing.SlotId.ToString() : "",
            placeholder: knownSlots.Count > 0
                ? $"known: {string.Join(" ", knownSlots.Take(10))}"
                : "nothing seen yet — fire it in game",
            numeric: true);

        foreach (var t in Enum.GetValues<ShipSlotType>()) _category.Items.Add(t);
        _category.SelectedItem = Enum.TryParse<ShipSlotType>(existing?.Category ?? "", true, out var cat)
            ? cat
            : hex <= LoadoutView.WeaponHexes ? ShipSlotType.Gun : ShipSlotType.Undefined;

        foreach (var (label, _) in Roles) _role.Items.Add(label);
        int roleIndex = 0;
        if (Enum.TryParse<WeaponRole>(existing?.Role ?? "", true, out var savedRole))
            roleIndex = Math.Max(0, Array.FindIndex(Roles, r => r.Role == savedRole));
        _role.SelectedIndex = roleIndex;

        _name.Text = existing?.Name ?? "";
        _level.Number = existing?.Level is > 0 ? existing.Level : null;
        _max.Number = existing?.MaxRange;
        _optimal.Number = existing?.OptimalRange;
        _min.Number = existing?.MinRange;
        _reload.Number = existing?.Cooldown;
        _power.Number = existing?.PowerCost;
        _ammo.Text = existing?.Ammo ?? "";
        _enabled.Checked = existing?.Enabled ?? true;

        BuildLayout(existing);

        _slotId.Committed += (_, _) => ShowWireFacts();
        ShowWireFacts();

        _bot.AbilitySeen += OnAbilitySeen;
        FormClosed += (_, _) => _bot.AbilitySeen -= OnAbilitySeen;
    }

    // ---------------------------------------------------------------- layout

    private void BuildLayout(SavedSlot? existing)
    {
        var body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(14, 10, 14, 4) };

        int y = 6;
        void Row(string caption, Control field, string? hint = null)
        {
            var label = new Label
            {
                Text = caption, Font = Theme.Header, ForeColor = Theme.Faint,
                Bounds = new Rectangle(0, y + 6, 140, 18),
            };
            field.Bounds = new Rectangle(146, y, hint is null ? 360 : 190, 26);
            body.Controls.Add(label);
            body.Controls.Add(field);

            if (hint is not null)
                body.Controls.Add(new Label
                {
                    Text = hint, Font = Theme.UiSmall, ForeColor = Theme.Faint,
                    Bounds = new Rectangle(342, y + 6, 168, 18),
                });

            y += 32;
        }

        // --- identity: which slot this hex actually is
        _bind.Text = "Bind by firing";
        _bind.Width = 118;
        _bind.Click += (_, _) => ToggleArm();

        var test = new FlatButton { Text = "Test fire", Width = 88 };
        test.Click += (_, _) => TestFire();

        Row("ABILITY / SLOT ID", _slotId, null);
        _slotId.Width = 92;
        _bind.Bounds = new Rectangle(246, y - 32, 118, 26);
        test.Bounds = new Rectangle(370, y - 32, 88, 26);
        body.Controls.Add(_bind);
        body.Controls.Add(test);

        _wire.Bounds = new Rectangle(146, y, 380, 34);
        _wire.Font = Theme.MonoSmall;
        _wire.ForeColor = Theme.Muted;
        body.Controls.Add(_wire);
        y += 40;

        body.Controls.Add(new Panel
        {
            Bounds = new Rectangle(0, y, 520, 1), BackColor = Theme.Border,
        });
        y += 12;

        // --- the card, as printed in game
        Row("NAME", _name);
        Row("CATEGORY", _category, "which hex group it belongs to");
        Row("LEVEL", _level, "as shown on the card");
        // Full width: the role labels say what the bot will do with the slot, and truncating
        // them would hide the only thing on this form that changes the bot's behaviour.
        Row("BOT USES IT AS", _role);
        Row("MAX RANGE", _max, "units — blank if you don't know");
        Row("OPTIMAL RANGE", _optimal, "units");
        Row("MIN RANGE", _min, "units");
        Row("RELOAD", _reload, "seconds");
        Row("POWER COST", _power, "points per shot");
        Row("AMMO LOADED", _ammo);

        _enabled.Bounds = new Rectangle(146, y + 2, _enabled.Width, _enabled.Height);
        body.Controls.Add(_enabled);
        y += 34;

        var note = new Label
        {
            Bounds = new Rectangle(0, y, 520, 46),
            Font = Theme.UiSmall,
            ForeColor = Theme.Faint,
            Text = "Numbers you type are only used where the server publishes none — its own "
                 + "slot stats are what it will actually enforce, so they win. The role is the "
                 + "other way round: yours is final.",
        };
        body.Controls.Add(note);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = 48, FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(10), BackColor = Theme.Panel,
        };
        var save = new FlatButton { Text = "Save", Width = 92, Primary = true };
        save.Click += (_, _) => Commit();
        var cancel = new FlatButton { Text = "Cancel", Width = 92, DialogResult = DialogResult.Cancel };
        buttons.Controls.AddRange([save, cancel]);

        if (existing is not null)
        {
            var clear = new FlatButton { Text = "Clear hex", Width = 96, DialogResult = DialogResult.Abort };
            clear.Tint = Theme.Bad;
            buttons.Controls.Add(clear);
        }

        CancelButton = cancel;

        Controls.Add(body);
        Controls.Add(buttons);
    }

    // ---------------------------------------------------------------- binding

    private void ToggleArm()
    {
        _arming = !_arming;
        _bind.Text = _arming ? "Waiting — fire it…" : "Bind by firing";
        _bind.Tint = _arming ? Theme.Good : Color.Empty;
        _bind.Invalidate();
    }

    /// <summary>
    /// The next ability the real client fires becomes this hex. Arrives on the proxy's thread,
    /// so it has to be marshalled before it touches a control.
    /// </summary>
    private void OnAbilitySeen(ushort id)
    {
        if (!_arming || IsDisposed) return;
        if (InvokeRequired) { try { BeginInvoke(() => OnAbilitySeen(id)); } catch { } return; }
        if (!_arming) return;

        _arming = false;
        _bind.Text = "Bind by firing";
        _bind.Tint = Color.Empty;
        _slotId.Text = id.ToString();
        ShowWireFacts();
    }

    private void TestFire()
    {
        if (SlotIdValue() is not { } id)
        {
            MessageBox.Show("Put an ability id in first, or use \"Bind by firing\".",
                "Nothing to fire", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        _ = _bot.TestFireAsync(id);
    }

    private ushort? SlotIdValue()
    {
        var n = _slotId.Number;
        return n is > 0 and <= ushort.MaxValue ? (ushort)n.Value : null;
    }

    /// <summary>
    /// Everything the wire already says about this id, so the numbers you type can be checked
    /// against it — and so you can tell two similar slots apart before committing a name.
    /// </summary>
    private void ShowWireFacts()
    {
        if (SlotIdValue() is not { } id)
        {
            _wire.ForeColor = Theme.Faint;
            _wire.Text = "No id yet. Fire the ability in game with \"Bind by firing\" armed.";
            return;
        }

        var w = _bot.Weapons.Find(id);
        var live = _world.MyLoadout?.Slot(id);

        var parts = new List<string>();
        if (live is not null)
            parts.Add(live.Filled ? $"server: item {live.SystemGuid}" : "server: slot is empty");
        if (live is { ConsumableGuid: > 0 }) parts.Add($"ammo {live.ConsumableGuid}");
        if (live is { Inoperable: true }) parts.Add("INOPERABLE");

        if (w is not null)
        {
            parts.Add($"bot: {w.Role}");
            if (w.StatMaxRange is { } m) parts.Add($"{m:F0}u");
            if (w.StatCooldown is { } c) parts.Add($"{c:F1}s");
            if (w.StatPowerCost is { } p) parts.Add($"{p:F0} power");
            parts.Add($"[{w.Source}]");
        }

        _wire.ForeColor = live is { Inoperable: true } ? Theme.Bad : Theme.Muted;
        _wire.Text = parts.Count == 0
            ? "The bot has never seen this id. It will still be used once you save."
            : string.Join("  ·  ", parts);
    }

    // ---------------------------------------------------------------- commit

    private void Commit()
    {
        var id = SlotIdValue();
        if (id is null)
        {
            var go = MessageBox.Show(
                "This hex has no ability id, so the bot cannot fire it — it will only be a label.\n\n" +
                "Save it anyway?", "No ability id", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (go != DialogResult.Yes) return;
        }

        var category = (ShipSlotType)(_category.SelectedItem ?? ShipSlotType.Undefined);
        var role = Roles[Math.Max(0, _role.SelectedIndex)].Role;

        Result = new SavedSlot
        {
            SlotId = id ?? 0,
            Hex = _hex,
            Name = _name.Text.Trim(),
            Category = category.ToString(),
            Level = (byte)Math.Clamp((int)(_level.Number ?? 0), 0, 255),
            Role = role?.ToString() ?? "",
            MaxRange = _max.Number,
            OptimalRange = _optimal.Number,
            MinRange = _min.Number,
            Cooldown = _reload.Number,
            PowerCost = _power.Number,
            Ammo = _ammo.Text.Trim(),
            Enabled = _enabled.Checked,
            SystemGuid = id is null ? 0 : _world.MyLoadout?.Slot(id.Value)?.SystemGuid ?? 0,
        };

        DialogResult = DialogResult.OK;
        Close();
    }
}
