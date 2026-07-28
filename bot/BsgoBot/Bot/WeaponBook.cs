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
    bool Enabled,
    /// <summary>The catalogue guid that was installed in this slot when you described it, so a
    /// refit — or a different ship — can be told apart from the loadout you actually meant.
    /// 0 when it was never recorded, which means the declaration cannot be checked.</summary>
    uint SystemGuid = 0);

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

    /// <summary>
    /// The catalogue guid that was in this slot when you described it. 0 if never recorded.
    ///
    /// What makes a declaration checkable. <see cref="RoleFromUser"/> is the strongest evidence
    /// the bot has and it outranks the server — which is right while it describes the ship you
    /// are actually flying, and dangerous the moment it does not. Slot 3 on a Raptor is damage
    /// control; slot 3 on a Vanir is whatever that hull put there. Same id, same saved profile,
    /// completely different module, and the bot would have gone on firing it at its own hull on
    /// the strength of a sentence typed about a different ship.
    /// </summary>
    public uint DeclaredSystemGuid { get; set; }

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

    // The catalogue tier. Resolved from slot → fitted system → ability card, and the ability's
    // ItemBuffAdd is the very block the server reads to decide whether a shot is in range and
    // what it costs. So this is not a better guess than yours — it is the same authority as the
    // stat stream, available on servers that never send one.
    //
    // It sits above what you typed for exactly that reason, and below the live stats because
    // buffs and modules can move the real numbers away from the printed card.

    public float? CardMaxRange { get; set; }
    public float? CardMinRange { get; set; }
    public float? CardOptimalRange { get; set; }
    public float? CardCooldown { get; set; }
    public float? CardPowerCost { get; set; }

    /// <summary>Firing arc half-angle in degrees; 0 means omnidirectional. Catalogue only — the
    /// stat stream carries it too, but nothing has ever published one here.</summary>
    public float? CardAngle { get; set; }

    public float? UserMaxRange { get; set; }
    public float? UserMinRange { get; set; }
    public float? UserOptimalRange { get; set; }
    public float? UserCooldown { get; set; }
    public float? UserPowerCost { get; set; }

    public float? MaxRange => StatMaxRange ?? CardMaxRange ?? UserMaxRange;
    public float? MinRange => StatMinRange ?? CardMinRange ?? UserMinRange;
    public float? OptimalRange => StatOptimalRange ?? CardOptimalRange ?? UserOptimalRange;
    public float? Cooldown => StatCooldown ?? CardCooldown ?? UserCooldown;
    public float? PowerCost => StatPowerCost ?? CardPowerCost ?? UserPowerCost;

    /// <summary>
    /// True while <see cref="Kind"/> is a guess rather than something observed.
    ///
    /// Set only by the catalogue path, which has no way to tell a cast from a toggle. Cleared
    /// the first time the ability is actually seen being fired, because that settles it.
    /// </summary>
    public bool KindAssumed { get; set; }

    /// <summary>Where the reach in force came from, for the panel and the log.</summary>
    public string RangeSource =>
        StatMaxRange is not null ? "server stats"
        : CardMaxRange is not null ? "catalogue"
        : UserMaxRange is not null ? "you"
        : "unknown";

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
    /// <summary>
    /// The abilities to fire for a given job.
    ///
    /// An <see cref="WeaponRole.Unknown"/> ability — one we have only ever watched you fire, with
    /// no role attached — counts as a weapon <b>only while you have not described your loadout</b>.
    /// That fallback exists so a fresh profile can still shoot something; it is not a licence to
    /// keep firing ids left over from another ship.
    ///
    /// It had become exactly that. Ability ids persist in <c>bot.json</c> across refits, so the
    /// book accumulated a dozen roleless ids and fired every one of them at every target. On a
    /// hull where those ids are real slots holding an engine or an armour plate, that is a cast
    /// the server has no sensible answer to — and the connection dropped seconds after each
    /// volley. Once you have declared even one slot, the declaration is the list.
    /// </summary>
    public List<Weapon> For(WeaponRole role)
    {
        bool shooting = role is WeaponRole.Combat or WeaponRole.Mining;
        lock (_gate)
        {
            bool anyDeclared = _weapons.Values.Any(w => w.RoleFromUser);
            bool guessAllowed = shooting && !anyDeclared;

            return _weapons.Values
                .Where(w => w.Enabled)
                // The closed world the comment above promises, which this used to only half do.
                //
                // It filtered out abilities whose role was UNKNOWN, and let through every one the
                // bot was merely CONFIDENT about — which is the wrong half. #4 was not an unknown
                // slot the bot wondered about; it was an armour plate the bot was certain was a
                // repair module, and certainty is exactly what walked it past the guard. Nothing
                // you never placed on a hex is fired now, however sure the bot is about it.
                .Where(w => !anyDeclared || w.RoleFromUser)
                .Where(w => w.Role == role || (w.Role == WeaponRole.Unknown && guessAllowed))
                .OrderBy(w => w.AbilityId)
                .ToList();
        }
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
    /// <summary>
    /// Drops declarations that describe a module which is no longer in the slot.
    /// </summary>
    /// <remarks>
    /// A declaration is the strongest evidence the bot has. That is correct while it describes
    /// the ship being flown and actively harmful the moment it does not: swap hull, and slot 3 is
    /// still called "damage control, fire it at yourself" by a profile written for a different
    /// ship. The saved catalogue guid is what tells the two apart, and it was already being
    /// recorded — the loadout panel even drew a "refitted" marker with it — but nothing in the
    /// bot's decision path had ever consulted it.
    ///
    /// <para>Only fires when the server has actually stated what is in the slot and the guid
    /// disagrees. An unknown guid on either side proves nothing and is left alone.</para>
    /// </remarks>
    /// <returns>The abilities whose declaration was withdrawn.</returns>
    public List<Weapon> DropStaleDeclarations(Func<ushort, uint?> installedSystemGuid)
    {
        var stale = new List<Weapon>();
        lock (_gate)
        {
            foreach (var w in _weapons.Values)
            {
                if (!w.RoleFromUser || w.DeclaredSystemGuid == 0) continue;

                uint? live = installedSystemGuid(w.AbilityId);
                if (live is null or 0 || live == w.DeclaredSystemGuid) continue;

                w.RoleFromUser = false;
                w.Role = WeaponRole.Unknown;
                w.RoleFromStats = false;
                w.DeclaredSystemGuid = 0;
                stale.Add(w);
            }
        }
        return stale;
    }

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

                // Watching it fire settles cast-versus-toggle, which the catalogue could not.
                // An assumed Cast that turns out to be a Toggle is corrected on the line above;
                // either way the guess is over.
                if (w.KindAssumed) { w.Kind = kind; w.KindAssumed = false; }
                // A self-cast says an ability is aimed at your own ship. It does NOT say the
                // ability repairs anything — a shield, a cloak, a power booster and a damage
                // control module are all self-cast, and this used to relabel every one of them
                // Repair, including slots the server had already classified. SelfRepairAsync
                // then fired the lot at the hull and bsgo.fun closed the connection.
                //
                // So it now fills a genuine blank only, and never argues with a classification
                // that came from the stat stream or the catalogue.
                if (w.RoleFromUser) { /* you already said what this is */ }
                else if (role == WeaponRole.Repair && w.Role == WeaponRole.Unknown && !w.RoleFromStats)
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
            // You have already pointed at the scanner. A second one, learned from a reply that
            // happened to land while something else was cast, is not a discovery — it is a rival
            // for the same job with none of the numbers you typed in, and whichever of the two
            // came out of the dictionary first then decided whether scanning worked at all.
            if (_weapons.Values.Any(x => x.Role == WeaponRole.Scanner && x.RoleFromUser
                                      && x.AbilityId != abilityId))
                return null;

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
            w.DeclaredSystemGuid = d.SystemGuid;
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
    /// <summary>
    /// Fills every weapon's numbers from the server's own catalogue.
    ///
    /// The chain is <c>slot id → fitted system guid → ShipSystem card → ShipAbility card</c>, and
    /// it needs nothing from you: the slot list says what is fitted, and the cards say what it
    /// does. Ranges, reload, power cost, firing arc and the role all arrive together.
    ///
    /// This is what makes the typed-in numbers optional and <c>FallbackRange</c> nearly dead. It
    /// only ever adds to the card tier, so a live stat still wins and anything you typed is still
    /// there underneath as a last resort.
    /// </summary>
    public int RefreshFromCatalogue(WorldState world, Cards.CatalogueSpy cards)
    {
        int learned = 0;

        foreach (var slot in world.MySlots())
        {
            if (!slot.Filled) continue;

            var system = cards.System(slot.SystemGuid);
            if (system is null) continue;

            var ability = system.AbilityCardGuids
                .Select(cards.Ability)
                .FirstOrDefault(a => a is not null);
            if (ability is null) continue;

            lock (_gate)
            {
                if (!_weapons.TryGetValue(slot.SlotId, out var w))
                {
                    // Cast is an ASSUMPTION, and the one thing here that is not read off a card.
                    // Nothing in ShipAbilityCard that we have transcribed says cast-versus-toggle
                    // — Affect only distinguishes single from area — so a toggle-fired mining
                    // laser learned this way is cast at instead of switched on, which means it
                    // never actually runs.
                    //
                    // It self-corrects the moment the ability is seen being toggled in the real
                    // client (NoteShot upgrades Cast to Toggle and never the other way), and it
                    // is flagged so the loadout panel can say so rather than presenting a guess
                    // as though it came from the server.
                    w = new Weapon
                    {
                        AbilityId = slot.SlotId,
                        Kind = WeaponKind.Cast,
                        KindAssumed = true,
                        Source = "the catalogue",
                    };
                    _weapons[slot.SlotId] = w;
                    learned++;
                }

                w.CardMaxRange = Positive(ability.MaxRange);
                w.CardMinRange = ability.MinRange;          // 0 is a real minimum, keep it
                w.CardOptimalRange = Positive(ability.OptimalRange);
                w.CardCooldown = Positive(ability.Cooldown);
                w.CardPowerCost = Positive(ability.PowerCost);
                w.CardAngle = ability.Angle;

                // What it IS, stated rather than inferred from the shape of its stats. Yours
                // still wins — RoleFromUser is the top of the chain and this must not disturb it.
                if (!w.RoleFromUser && RoleForAction(ability.EffectiveAction) is { } role
                    && w.Role != role)
                {
                    w.Role = role;
                    w.RoleFromStats = true;
                }

                // Affect is stated on the card, so the area/single question no longer needs
                // proving by experiment.
                w.Area ??= ability.Affect == Cards.ShipAbilityAffect.Area;
            }
        }

        return learned;
    }

    private static float? Positive(float? v) => v is > 0 ? v : null;

    /// <summary>Maps the server's own action type onto what the bot does with a slot.</summary>
    private static WeaponRole? RoleForAction(Cards.AbilityActionType a) => a switch
    {
        Cards.AbilityActionType.FireMining => WeaponRole.Mining,
        Cards.AbilityActionType.ResourceScan => WeaponRole.Scanner,
        Cards.AbilityActionType.RestoreBuff => WeaponRole.Repair,
        Cards.AbilityActionType.FireCannon or Cards.AbilityActionType.FireMissle or
        Cards.AbilityActionType.FireTorpedo or Cards.AbilityActionType.FireLightMissile or
        Cards.AbilityActionType.FireHeavyMissile or Cards.AbilityActionType.FireShotgun or
        Cards.AbilityActionType.FireKillCannon or Cards.AbilityActionType.FireMachineGun or
        Cards.AbilityActionType.Flak or Cards.AbilityActionType.PointDefence => WeaponRole.Combat,
        Cards.AbilityActionType.None => null,
        _ => WeaponRole.Utility,
    };

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
