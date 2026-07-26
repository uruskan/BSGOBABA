using BsgoBot.Protocol;
using BsgoBot.World;

namespace BsgoBot.Bot;

/// <summary>How an ability is fired. The client picks this from the ability card's Launch
/// mode: Auto abilities are toggled on and retargeted, everything else is cast per shot.</summary>
public enum WeaponKind
{
    Cast = 0,      // GameOp.Request.CastSlotAbility, once per shot
    Toggle = 1,    // GameOp.Request.ToggleAbilityOn, then UpdateAbilityTargets to retarget
}

public enum WeaponRole
{
    /// <summary>Seen you fire it, but no stats to say what it is. Still worth firing —
    /// an ability you used on something is an ability that does something.</summary>
    Unknown = 0,
    Combat = 1,
    Mining = 2,
    /// <summary>The resource scanner. Not a weapon — it is never fired at anything that
    /// isn't a rock, and never counts towards "do I have a gun".</summary>
    Scanner = 3,
    /// <summary>
    /// A slot the server published stats for that deal no damage and advertise no weapon
    /// range — armour, boosts, ECM, and the scanner before we've identified it. Never fired
    /// as a weapon, but it IS the pool the scanner probe draws from.
    /// </summary>
    Utility = 4,
    /// <summary>
    /// Something you cast on yourself — Strike Damage Control and its relatives. Learned by
    /// watching you cast an ability at your own ship, which is a signal nothing else produces.
    /// Fired when the hull is hurt, never at a target.
    /// </summary>
    Repair = 5,
}

/// <summary>
/// What you typed into the loadout panel for one slot.
///
/// Everything here is optional. The point is not to replace what the server publishes — the
/// slot-stat stream is measured truth and still wins on numbers — but to settle the two things
/// no amount of sniffing can: which slot is which item, and what you want it used for.
/// </summary>
public sealed record SlotDeclaration(
    ushort SlotId,
    string Name,
    ShipSlotType Category,
    byte Level,
    WeaponRole? Role,
    float? MaxRange,
    float? OptimalRange,
    float? MinRange,
    float? Cooldown,
    float? PowerCost,
    string Ammo,
    bool Enabled);

public sealed class Weapon
{
    public ushort AbilityId { get; init; }
    public WeaponKind Kind { get; set; }
    public WeaponRole Role { get; set; }

    /// <summary>Where the knowledge came from, so the UI can be honest about it.</summary>
    public string Source { get; set; } = "";

    // ---------------------------------------------------------------- what you told us

    /// <summary>The item's name, as you read it off the card in game. Cosmetic to the bot and
    /// the whole point to you — "#7" and "Tornado-P" are not equally useful in a log line.</summary>
    public string Name { get; set; } = "";

    /// <summary>Which kind of slot this is. Never on the wire — it lives in the ship's
    /// catalogue card — so it is yours to state, and it decides where the hex is drawn.</summary>
    public ShipSlotType Category { get; set; }

    public byte Level { get; set; }

    /// <summary>The consumable you have loaded, by name. Records what "switch ammo" is set to.</summary>
    public string Ammo { get; set; } = "";

    /// <summary>
    /// True when <see cref="Role"/> came from you rather than from a guess or a stat sweep.
    ///
    /// This is the strongest evidence there is, and it outranks everything — including the
    /// server's own classification. A slot the stats call Utility because it advertises no
    /// damage really can be your damage-control module, and you are the one who can see the card.
    /// </summary>
    public bool RoleFromUser { get; set; }

    /// <summary>
    /// True only when <see cref="Role"/> itself came from the per-slot stat stream — the server
    /// stating a damage figure — rather than from watching you fire the thing at something.
    ///
    /// This exists because <see cref="Source"/> is a running list of every source that ever
    /// touched the weapon, so "contains slot stats" does NOT mean "stats decided the role". The
    /// scanner is the case that broke: firing it at a rock labelled it Mining from your shot, a
    /// later stat sweep appended "slot stats" to Source without changing the role, and the guard
    /// in <see cref="WeaponBook.MarkScanner"/> then refused the server's own scan reply forever.
    /// </summary>
    public bool RoleFromStats { get; set; }

    // ---------------------------------------------------------------- numbers
    //
    // Two independent sets, never merged in place. The server's are measured — they are what it
    // will actually enforce when the bot pulls the trigger — so they win wherever they exist,
    // and yours fill the gaps for the servers that publish nothing. Keeping them apart is what
    // lets the panel say which of the two is in force, instead of quietly overwriting one with
    // the other and leaving you to wonder why the reach changed.

    public float? StatMaxRange { get; set; }
    public float? StatMinRange { get; set; }
    public float? StatOptimalRange { get; set; }
    public float? StatCooldown { get; set; }
    public float? StatPowerCost { get; set; }

    public float? UserMaxRange { get; set; }
    public float? UserMinRange { get; set; }
    public float? UserOptimalRange { get; set; }
    public float? UserCooldown { get; set; }
    public float? UserPowerCost { get; set; }

    public float? MaxRange => StatMaxRange ?? UserMaxRange;
    public float? MinRange => StatMinRange ?? UserMinRange;
    public float? OptimalRange => StatOptimalRange ?? UserOptimalRange;
    public float? Cooldown => StatCooldown ?? UserCooldown;
    public float? PowerCost => StatPowerCost ?? UserPowerCost;

    /// <summary>True when every number in force came from you, i.e. the server published none.</summary>
    public bool NumbersAreYours =>
        StatMaxRange is null && StatCooldown is null && StatPowerCost is null
        && (UserMaxRange is not null || UserCooldown is not null || UserPowerCost is not null);

    /// <summary>
    /// Whether this ability affects an area rather than one target. Null until proven.
    ///
    /// It decides how many ids a cast may legitimately carry. An Area ability is expected to
    /// carry every valid object in range — the client builds exactly that list in
    /// ShipAbility.GetObjectsWithinAOE — while a Selected one must carry exactly one, and the
    /// server logs anything else as cheating. So this is only ever set from proof, never guessed.
    /// </summary>
    public bool? Area { get; set; }

    /// <summary>True while we believe a toggle weapon is firing.</summary>
    public bool ToggledOn { get; set; }
    public uint ToggleTarget { get; set; }
    public DateTime LastFired { get; set; } = DateTime.MinValue;

    /// <summary>Whether this weapon is allowed to fire (UI checkbox).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>What to call this in a log line or the diagnostics dump.</summary>
    public string Label => Name.Length > 0 ? $"#{AbilityId} {Name}" : $"#{AbilityId}";

    public string Describe()
    {
        string range = MaxRange is { } m ? $"{m:F0}u" : "range ?";
        string cd = Cooldown is { } c ? $"{c:F1}s" : "cd ?";
        return $"{Label} {Role} {Kind} {range} {cd} [{Source}]";
    }
}

/// <summary>
/// Everything the bot knows about your weapons, from two independent sources:
///
///  1. Your own outgoing traffic. Any ability you cast or toggle is, by definition, a real
///     ability id for the ship you are actually flying. This works on every server.
///
///  2. The per-slot stat stream (PlayerProtocol Reply.Stats -> StatUpdateType.SlotStat).
///     The server publishes each slot's damage, range, cooldown and power cost there, which
///     is where a real answer to "what is my range" lives. Servers that don't send slot
///     stats simply fall back to source 1.
///
/// Neither source needs the catalogue, which the bot deliberately does not parse.
/// </summary>
public sealed class WeaponBook
{
    private readonly Dictionary<ushort, Weapon> _weapons = new();
    private readonly Lock _gate = new();

    public event Action<Weapon, bool>? Learned;   // (weapon, isNew)

    public int Count { get { lock (_gate) return _weapons.Count; } }

    public List<Weapon> All()
    {
        lock (_gate) return _weapons.Values.OrderBy(w => w.AbilityId).ToList();
    }

    /// <summary>
    /// Weapons usable for a role. An ability we've only ever watched you fire counts towards a
    /// shooting role — firing something we haven't identified is how the bot works at all — but
    /// never towards <see cref="WeaponRole.Scanner"/> or <see cref="WeaponRole.Utility"/>, which
    /// hold only what the server told us.
    /// </summary>
    public List<Weapon> For(WeaponRole role)
    {
        bool shooting = role is WeaponRole.Combat or WeaponRole.Mining;
        lock (_gate)
            return _weapons.Values
                .Where(w => w.Enabled && (w.Role == role || (w.Role == WeaponRole.Unknown && shooting)))
                .OrderBy(w => w.AbilityId)
                .ToList();
    }

    /// <summary>
    /// Abilities worth testing as a possible scanner.
    ///
    /// A utility slot only qualifies if it publishes a reach: scanning happens at a distance and
    /// the server reads the ability's own MaxRange to decide whether the rock is close enough,
    /// so anything with no range is a self-buff, an armour plate or a consumable. Skipping those
    /// matters — several carry limited charges, and burning one to learn it isn't a scanner is a
    /// bad trade. Abilities we've only ever watched you fire are tried last.
    /// </summary>
    public List<Weapon> ProbeCandidates()
    {
        lock (_gate)
            return _weapons.Values
                .Where(w => w.Enabled)
                // Nothing you have already identified is worth a test cast. Declaring your
                // loadout is the fastest way to stop the probe spending consumables on it.
                .Where(w => !w.RoleFromUser)
                .Where(w => (w.Role == WeaponRole.Utility && w.MaxRange is > 0)
                         || w.Role == WeaponRole.Unknown)
                .OrderBy(w => w.Role == WeaponRole.Utility ? 0 : 1)
                .ThenBy(w => w.AbilityId)
                .ToList();
    }

    public Weapon? Find(ushort abilityId)
    {
        lock (_gate) return _weapons.GetValueOrDefault(abilityId);
    }

    /// <summary>
    /// Records an ability seen in your own outgoing traffic. <paramref name="role"/> is what
    /// you were aiming at, which is weak evidence — leave it Unknown and the weapon stays
    /// eligible for both roles until the slot stats say otherwise. This used to assume Combat
    /// for everything, which is why mining mode never recognised a laser you had just fired.
    /// </summary>
    public Weapon Observe(ushort abilityId, WeaponKind kind, WeaponRole role = WeaponRole.Unknown)
    {
        Weapon w;
        bool isNew;
        lock (_gate)
        {
            isNew = !_weapons.TryGetValue(abilityId, out var existing);
            if (isNew)
            {
                w = new Weapon { AbilityId = abilityId, Kind = kind, Role = role, Source = "your shot" };
                _weapons[abilityId] = w;
            }
            else
            {
                w = existing!;
                // A toggle observation is stronger evidence than a cast one: only the Auto
                // launch mode ever produces ToggleAbilityOn.
                if (kind == WeaponKind.Toggle) w.Kind = WeaponKind.Toggle;
                // A self-cast is proof, not a guess: nothing else in the game targets your own
                // ship. It outranks the Utility label a stat sweep hands out by default.
                if (w.RoleFromUser) { /* you already said what this is */ }
                else if (role == WeaponRole.Repair && w.Role is WeaponRole.Unknown or WeaponRole.Utility)
                    w.Role = WeaponRole.Repair;
                else if (w.Role == WeaponRole.Unknown) w.Role = role;
                if (!w.Source.Contains("your shot"))
                    w.Source = string.IsNullOrEmpty(w.Source) ? "your shot" : w.Source + " + your shot";
            }
        }
        Learned?.Invoke(w, isNew);
        return w;
    }

    /// <summary>
    /// Marks an ability as the resource scanner, having watched the server answer a scan for
    /// the rock it was cast at. Refuses to relabel anything the slot stats proved is a weapon:
    /// a scan reply that merely happened to land while a cannon was firing must not cost you
    /// the cannon.
    /// </summary>
    public Weapon? MarkScanner(ushort abilityId)
    {
        Weapon w;
        lock (_gate)
        {
            if (!_weapons.TryGetValue(abilityId, out var existing))
            {
                w = new Weapon { AbilityId = abilityId, Kind = WeaponKind.Cast, Role = WeaponRole.Scanner, Source = "server scan reply" };
                _weapons[abilityId] = w;
            }
            else
            {
                w = existing;
                // You outrank the reply: if you declared this slot a gun, a scan answer that
                // happened to land while it was firing does not get to relabel it.
                if (w.RoleFromUser) return null;
                // Only a role the STATS decided can outrank a scan reply. A role inferred from
                // watching you fire it at a rock cannot: aiming at an asteroid is exactly what a
                // scanner does, so that inference is precisely the one the reply is correcting.
                if (w.Role is WeaponRole.Combat or WeaponRole.Mining && w.RoleFromStats)
                    return null;
                if (w.Role == WeaponRole.Scanner) return null;

                w.Role = WeaponRole.Scanner;
                if (!w.Source.Contains("server scan reply"))
                    w.Source = string.IsNullOrEmpty(w.Source) ? "server scan reply" : w.Source + " + server scan reply";
            }
        }
        Learned?.Invoke(w, true);
        return w;
    }

    /// <summary>Restores abilities remembered from a previous session.</summary>
    public void Restore(IEnumerable<(ushort Id, WeaponKind Kind, WeaponRole Role, bool Enabled)> saved)
    {
        lock (_gate)
        {
            foreach (var (id, kind, role, enabled) in saved)
            {
                if (_weapons.ContainsKey(id)) continue;
                _weapons[id] = new Weapon
                {
                    AbilityId = id, Kind = kind, Role = role,
                    Enabled = enabled, Source = "remembered",
                };
            }
        }
    }

    /// <summary>
    /// Records what you said is in a slot.
    ///
    /// This is the one input the bot treats as settled. Everything else it knows about a slot is
    /// an inference — from a damage figure, from what you happened to be aiming at — and every
    /// one of them fails on the same case: an ability that deals no damage and advertises no
    /// range looks identical whether it is a damage-control module, an armour plate or the
    /// resource scanner. You can read the card. So a role you declare is never revised, and the
    /// numbers you type are used wherever the server publishes none.
    ///
    /// A slot id the bot has never seen is created outright, which is what makes it possible to
    /// declare a module before you have ever fired it.
    /// </summary>
    public Weapon Declare(SlotDeclaration d)
    {
        Weapon w;
        bool isNew;
        lock (_gate)
        {
            isNew = !_weapons.TryGetValue(d.SlotId, out var existing);
            if (isNew)
            {
                w = new Weapon { AbilityId = d.SlotId, Kind = WeaponKind.Cast, Source = "you declared it" };
                _weapons[d.SlotId] = w;
            }
            else
            {
                w = existing!;
                if (!w.Source.Contains("you declared it"))
                    w.Source = string.IsNullOrEmpty(w.Source) ? "you declared it" : w.Source + " + you declared it";
            }

            w.Name = d.Name;
            w.Category = d.Category;
            w.Level = d.Level;
            w.Ammo = d.Ammo;
            w.Enabled = d.Enabled;

            if (d.Role is { } role)
            {
                w.Role = role;
                w.RoleFromUser = true;
            }
            else if (w.RoleFromUser)
            {
                // You cleared the override. Fall back to whatever the wire says, and let the
                // next stat sweep re-decide rather than leaving your old answer frozen in.
                w.RoleFromUser = false;
                w.Role = WeaponRole.Unknown;
                w.RoleFromStats = false;
            }

            w.UserMaxRange = d.MaxRange;
            w.UserMinRange = d.MinRange;
            w.UserOptimalRange = d.OptimalRange;
            w.UserCooldown = d.Cooldown;
            w.UserPowerCost = d.PowerCost;
        }

        Learned?.Invoke(w, isNew);
        return w;
    }

    /// <summary>
    /// Replaces the whole declared layer with <paramref name="declarations"/>.
    ///
    /// Full-state rather than incremental, because the interesting case is the one an
    /// incremental update misses: clearing a hex. A slot you stop declaring has to go back to
    /// being worked out from the wire, and a slot you had switched off has to switch back on —
    /// otherwise deleting a declaration would leave the bot obeying it until the next restart.
    /// </summary>
    public void SyncDeclarations(IReadOnlyCollection<SlotDeclaration> declarations)
    {
        var declared = declarations.Select(d => d.SlotId).ToHashSet();

        List<Weapon> released;
        lock (_gate)
            released = _weapons.Values.Where(w => w.RoleFromUser && !declared.Contains(w.AbilityId)).ToList();

        foreach (var w in released)
        {
            lock (_gate)
            {
                w.RoleFromUser = false;
                w.Role = WeaponRole.Unknown;
                w.RoleFromStats = false;
                w.Enabled = true;
                w.Name = "";
                w.Ammo = "";
                w.Category = default;
                w.Level = 0;
                w.UserMaxRange = w.UserMinRange = w.UserOptimalRange = null;
                w.UserCooldown = w.UserPowerCost = null;
            }
        }

        foreach (var d in declarations) Declare(d);
    }

    /// <summary>
    /// Folds the per-slot stat stream in: discovers weapon slots we've never seen fire, and
    /// attaches real ranges and cooldowns to the ones we have.
    ///
    /// Slots that deal no damage are registered too, as <see cref="WeaponRole.Utility"/>.
    /// Dropping them used to look harmless — they are not weapons, so nothing would fire them —
    /// but it also meant the scanner never appeared in the book at all, and the probe that
    /// hunts for it had an empty list to search.
    /// </summary>
    public void RefreshFromStats(WorldState world)
    {
        foreach (var slot in world.KnownSlots())
        {
            var stats = world.SlotStats(slot);
            if (stats.Count == 0) continue;

            var role = ClassifySlot(stats);
            if (role == WeaponRole.Unknown) role = WeaponRole.Utility;

            float? max = First(stats, ObjectStat.CannonMaxRange, ObjectStat.MissileMaxRange,
                                      ObjectStat.MiningMaxRange, ObjectStat.MaxRange);
            float? min = First(stats, ObjectStat.CannonMinRange, ObjectStat.MissileMinRange,
                                      ObjectStat.MiningMinRange, ObjectStat.MinRange);
            float? opt = First(stats, ObjectStat.CannonOptimalRange, ObjectStat.MiningOptimalRange,
                                      ObjectStat.OptimalRange);
            float? cd = First(stats, ObjectStat.CannonCooldown, ObjectStat.MissileCooldown,
                                     ObjectStat.MiningCooldown, ObjectStat.Cooldown);
            float? cost = First(stats, ObjectStat.CannonPowerPointCost, ObjectStat.MissilePowerPointCost,
                                       ObjectStat.MiningPowerPointCost, ObjectStat.PowerPointCost);

            var kind = world.IsToggleSlot(slot) ? WeaponKind.Toggle : (WeaponKind?)null;

            Weapon w;
            bool isNew;
            lock (_gate)
            {
                isNew = !_weapons.TryGetValue(slot, out var existing);
                if (isNew)
                {
                    w = new Weapon
                    {
                        AbilityId = slot, Kind = kind ?? WeaponKind.Cast, Role = role,
                        Source = "slot stats", RoleFromStats = true,
                    };
                    _weapons[slot] = w;
                }
                else
                {
                    w = existing!;
                    // The server's own stats outrank anything we inferred from what you shot at,
                    // including a role remembered from a previous session. Two exceptions, both
                    // because a later stat sweep must not undo something better established:
                    // a scanner confirmed by a reply, and a weapon already classified FROM STATS
                    // that a partially-arrived sweep would otherwise demote to Utility.
                    //
                    // That last clause has to check RoleFromStats. Without it the guard also
                    // protected a Mining label that came from watching you fire the scanner at a
                    // rock, which is the one case where "no damage, no weapon range" is the
                    // better answer and needs to win.
                    bool demoting = role == WeaponRole.Utility
                                 && w.Role is WeaponRole.Combat or WeaponRole.Mining
                                 && w.RoleFromStats;
                    // A role you declared is never revised. You can see the item card; the
                    // stats can only see whether it advertises damage, which is exactly the
                    // question a repair module and an armour plate answer identically.
                    if (!w.RoleFromUser
                        && w.Role is not (WeaponRole.Scanner or WeaponRole.Repair) && !demoting)
                    {
                        w.Role = role;
                        w.RoleFromStats = true;
                    }
                    if (kind == WeaponKind.Toggle) w.Kind = WeaponKind.Toggle;
                    if (!w.Source.Contains("slot stats"))
                        w.Source = string.IsNullOrEmpty(w.Source) ? "slot stats" : w.Source + " + slot stats";
                }

                w.StatMaxRange = max ?? w.StatMaxRange;
                w.StatMinRange = min ?? w.StatMinRange;
                w.StatOptimalRange = opt ?? w.StatOptimalRange;
                w.StatCooldown = cd ?? w.StatCooldown;
                w.StatPowerCost = cost ?? w.StatPowerCost;
            }

            if (isNew) Learned?.Invoke(w, true);
        }
    }

    /// <summary>
    /// A slot is a weapon if it deals damage or advertises a weapon range. Buff, debuff and
    /// restore abilities have their own distinct range stats, so they never match here.
    /// </summary>
    private static WeaponRole ClassifySlot(IReadOnlyDictionary<ObjectStat, float> s)
    {
        if (Positive(s, ObjectStat.MiningMaxRange) || Positive(s, ObjectStat.MiningDamageHigh)
            || Positive(s, ObjectStat.DamageMining) || Positive(s, ObjectStat.MiningOptimalRange))
            return WeaponRole.Mining;

        if (Positive(s, ObjectStat.CannonMaxRange) || Positive(s, ObjectStat.MissileMaxRange)
            || Positive(s, ObjectStat.CannonDamageHigh) || Positive(s, ObjectStat.MissileDamageHigh)
            || Positive(s, ObjectStat.DamageHigh) || Positive(s, ObjectStat.DamageLow))
            return WeaponRole.Combat;

        return WeaponRole.Unknown;
    }

    private static bool Positive(IReadOnlyDictionary<ObjectStat, float> s, ObjectStat k) =>
        s.TryGetValue(k, out var v) && v > 0f;

    private static float? First(IReadOnlyDictionary<ObjectStat, float> s, params ObjectStat[] keys)
    {
        foreach (var k in keys)
            if (s.TryGetValue(k, out var v) && v > 0f) return v;
        return null;
    }

    /// <summary>Longest range among the weapons that will actually be used for a role.</summary>
    public float? BestRange(WeaponRole role)
    {
        var ranges = For(role).Select(w => w.MaxRange).Where(r => r is not null).Select(r => r!.Value).ToList();
        return ranges.Count == 0 ? null : ranges.Max();
    }

    public void SetEnabled(ushort abilityId, bool enabled)
    {
        lock (_gate) { if (_weapons.TryGetValue(abilityId, out var w)) w.Enabled = enabled; }
    }

    public void ResetToggles()
    {
        lock (_gate)
            foreach (var w in _weapons.Values) { w.ToggledOn = false; w.ToggleTarget = 0; }
    }

    public void Clear()
    {
        lock (_gate) _weapons.Clear();
    }
}
