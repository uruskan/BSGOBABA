using System.Numerics;
using BsgoBot.Net;
using BsgoBot.Protocol;
using BsgoBot.Proxy;
using BsgoBot.World;

namespace BsgoBot.Bot;

public enum FarmMode { Combat, Mining }

/// <summary>
/// The farm loop. Runs on a timer, reads the sniffed world, injects commands.
///
/// Everything it needs is learned from traffic: your ship id (from the player id the
/// launcher passes, confirmed by PlayerProtocol Reply.ID and matched against the PlayerShip
/// WhoIs), your weapons and their real ranges (from the per-slot stat stream, or from
/// watching you fire), and every object's position (from WhoIs for static objects, from
/// Move/SyncMove for everything that flies).
/// </summary>
public sealed class FarmBot
{
    private readonly WorldState _world;
    private readonly GameActions _act;
    private readonly GameProxy _proxy;
    private readonly System.Threading.Timer _timer;

    private uint _target;
    private uint _lockedTarget;
    private uint _subscribedTarget;

    /// <summary>An object you picked out of the contacts list yourself. Held ahead of the
    /// bot's own choice until it dies, leaves, or you clear it — the point of picking one by
    /// hand is that it should not be quietly swapped for whatever is nearest.</summary>
    private uint _pinned;

    // A fly-to / follow run. Like a dock run, it owns the ship while it lasts.
    private uint _followTarget;
    private bool _following;
    private bool _followHold;
    private float _followBest = float.MaxValue;
    private DateTime _followProgress;
    private bool _followLosingGround;
    private DateTime _lastRetarget = DateTime.MinValue;
    private DateTime _lastSteer = DateTime.MinValue;
    private DateTime _lastStatsSweep = DateTime.MinValue;
    private DateTime _lastThrottle = DateTime.MinValue;
    private bool _throttleOpen;
    private Gear _gear = Gear.Regular;
    private float _throttle;
    private int _busy;

    /// <summary>Fastest absolute throttle we've ever seen you send. See <see cref="TopSpeed"/>.</summary>
    private float _observedTopSpeed;

    // Approach watchdog: a target we never get closer to is unreachable, not just far.
    private uint _approachId;
    private DateTime _approachSince;
    private float _approachBestDistance;

    // The obstacle we are currently steering around, so the log gets one line per dodge.
    private uint _dodgeId;
    private DateTime _dodgeSince = DateTime.MinValue;

    private readonly Dictionary<uint, DateTime> _skip = new();
    private readonly HashSet<uint> _lootAsked = [];
    private readonly HashSet<uint> _facilityOrdered = [];
    private readonly Lock _gate = new();

    /// <summary>Rocks we've cast the scanner at, and when — so a rock whose reply never
    /// arrived is retried instead of being written off.</summary>
    private readonly Dictionary<uint, DateTime> _scanAsked = new();

    /// <summary>Abilities pointed at a rock, waiting to see whether a scan reply follows.
    /// Populated by your casts and by the bot's own deliberate probes — never by ordinary
    /// bot fire, or a scan landing mid-burst would relabel the gun that was shooting.</summary>
    private readonly Dictionary<uint, (ushort Ability, DateTime At)> _scanProbe = new();

    /// <summary>Abilities already tried once as a possible scanner this session.</summary>
    private readonly HashSet<ushort> _probed = [];
    private DateTime _lastProbe = DateTime.MinValue;

    /// <summary>Scans cast with nothing coming back. The server refuses a cast whose consumable
    /// is missing without saying so, so a scanner out of power cells looks exactly like a
    /// scanner that isn't working.</summary>
    private int _scansWithoutReply;
    private bool _ammoWarned;

    // ---- docking ---------------------------------------------------------------------
    private uint _dockTarget;
    private bool _docking;
    private DateTime _dockAsked = DateTime.MinValue;
    private DateTime _dockStarted = DateTime.MinValue;

    /// <summary>
    /// Distance at which YOU last docked successfully. The real limit is the station's
    /// OwnerCard.DockRange, which isn't on the wire — but the server logs an outright cheat
    /// warning for docking from too far out, so we'd rather copy a distance that worked than
    /// guess one that might not.
    /// </summary>
    private float _learnedDockRange;

    public WeaponBook Weapons { get; } = new();

    public bool Enabled { get; private set; }
    public FarmMode Mode { get; set; } = FarmMode.Combat;

    // ---- tuning -------------------------------------------------------------------
    /// <summary>Used when the server never told us a weapon's real range.</summary>
    public float FallbackRange { get; set; } = 3000f;

    /// <summary>Used when a weapon has no cooldown stat.</summary>
    public int FallbackFireIntervalMs { get; set; } = 900;

    /// <summary>Fly to targets that are out of range instead of just reporting them.</summary>
    public bool AutoApproach { get; set; } = true;

    /// <summary>Drop into the boost gear on long approaches. Costs tylium; the server puts you
    /// back in the regular gear by itself when the hold runs dry.</summary>
    public bool UseBoost { get; set; } = true;

    /// <summary>Boost only while we are this much further out than we need to be, so the ship
    /// isn't still doing boost speed when it arrives.</summary>
    public float BoostMargin { get; set; } = 1500f;

    /// <summary>
    /// Throttle used when the server never published the ship's Speed stat AND you have never
    /// flown at full throttle yourself. Servers that clamp will cut it down to the real maximum.
    /// </summary>
    public float FallbackSpeed { get; set; } = 100f;

    /// <summary>
    /// Your ship's real top speed, typed in. Beats everything else — the published stat, the
    /// fallback, and anything watched off your own throttle.
    ///
    /// Worth having because none of the automatic sources is reliable: the Speed stat is not
    /// published on every server, and watching your throttle only ever sees whatever you last
    /// happened to fly at. 0 leaves it on the automatic sources.
    /// </summary>
    public float TopSpeedOverride { get; set; }

    /// <summary>
    /// Your ship's speed in the boost gear, typed in. Beats the published BoostSpeed stat.
    ///
    /// This is never sent as a throttle — the gear applies it. It decides whether boosting is
    /// worth engaging, and it sizes the braking and obstacle-lookahead distances so they match
    /// the speed actually being flown. 0 leaves it on the published stat, and if the server
    /// doesn't publish one, the bot never boosts at all.
    /// </summary>
    public float BoostSpeedOverride { get; set; }

    /// <summary>Request and take loot from wrecks and cargo that come within reach.</summary>
    public bool AutoLoot { get; set; } = true;

    /// <summary>Loot anything closer than this. Cargo objects override it with their own radius.</summary>
    public float LootRange { get; set; } = 600f;

    /// <summary>Stop fighting below this fraction of hull.</summary>
    public float RetreatHull { get; set; } = 0.25f;

    /// <summary>
    /// Shoot back while mining. Without this the bot mines through an NPC attack run and dies
    /// holding station on a rock — it has guns, it just wasn't looking.
    /// </summary>
    public bool DefendSelf { get; set; } = true;

    /// <summary>A hostile closer than this is a threat. Anything actively targeting us counts
    /// at any distance.</summary>
    public float ThreatRange { get; set; } = 1500f;

    /// <summary>On low hull, actually run — away from the threat, at full throttle. Stopping
    /// dead in front of something shooting at you is not a retreat.</summary>
    public bool FleeWhenHurt { get; set; } = true;

    /// <summary>Run for a friendly outpost when hurt, rather than just away. Something that
    /// out-runs you can be out-run to a station; open space has nowhere to arrive at.</summary>
    public bool FleeToOutpost { get; set; } = true;

    /// <summary>Cast the repair module when the hull drops below this fraction.</summary>
    public float RepairAtHull { get; set; } = 0.8f;

    /// <summary>Use the repair module at all. It costs power the guns would otherwise get.</summary>
    public bool UseRepairAbility { get; set; } = true;

    /// <summary>Cadence for a repair ability the server published no cooldown for. Strike Damage
    /// Control reloads in 30 seconds, so this errs on the side of that.</summary>
    public int RepairIntervalMs { get; set; } = 30000;

    /// <summary>Shoot other players too, not just NPCs.</summary>
    public bool AttackPlayers { get; set; }

    /// <summary>
    /// Stay away from enemy weapon platforms and outposts, and never pick a rock or a target
    /// inside their reach. They out-range and out-gun a strike ship, they never lose interest,
    /// and unlike an NPC you cannot out-run one you have already flown up to.
    /// </summary>
    public bool AvoidHostileStations { get; set; } = true;

    /// <summary>
    /// How far to stay from an enemy emplacement. This is a deliberately conservative guess, not
    /// a number read off the wire: the server publishes slot stats for YOUR ship only, so the
    /// bot cannot know a given platform's actual reach. Raise it if something still shoots you.
    /// </summary>
    public float HostileStationKeepOut { get; set; } = 2500f;

    /// <summary>
    /// Which NPC kinds to hunt. Empty means all of them. Populated from the toolbar so you
    /// can farm fighters and leave the cruisers alone (or the reverse).
    /// </summary>
    public HashSet<SpaceEntityType> Prey { get; } = [];

    /// <summary>
    /// Only mine asteroids whose scan says they hold this resource. <see cref="ResourceType.Any"/>
    /// takes whatever is nearest. Unscanned asteroids are still tried, because the bot has no
    /// way to scan one yet — until it can, this filter only bites on rocks YOU scanned.
    /// </summary>
    public ResourceType WantedResource { get; set; } = ResourceType.Any;

    /// <summary>Also order a mining ship to the asteroid (costs resources) as well as
    /// firing your own mining laser.</summary>
    public bool UseMiningFacility { get; set; }

    /// <summary>How far inside the optimal range to sit. 0.6 means "60% of optimal" — close
    /// enough that drifting doesn't push us back out of the accurate band.</summary>
    public float CloseInFactor { get; set; } = 0.6f;

    /// <summary>Never try to sit closer than this. Flying into an object isn't an attack run.</summary>
    public float MinimumStandoff { get; set; } = 150f;

    /// <summary>
    /// Where to hold station on an asteroid, in units from its centre. An explicit number beats
    /// anything derived: the radius the server publishes is a bounding figure, and you can see
    /// how big these things actually are. 0 falls back to the derived standoff.
    ///
    /// Costs no accuracy to set low — the server's hit chance is flat at or below optimal range
    /// (HitchanceBasedOnThrottle.getChanceToHit) and only falls off beyond it.
    /// </summary>
    public float AsteroidStandoff { get; set; } = 179f;

    /// <summary>Same, for planetoids — which are enormous, and mined by ordering a mining ship
    /// rather than by shooting, so this is not clamped to weapon reach.</summary>
    public float PlanetoidStandoff { get; set; } = 1200f;

    /// <summary>How many times an object's reported radius to treat as solid. The radius the
    /// server publishes is a bounding figure, not the visual hull, so leave real margin.</summary>
    public float RadiusClearance { get; set; } = 3f;

    /// <summary>Ceiling on the braking zone. Full throttle right up to the stopping point means
    /// coasting straight through it, but the zone itself is worked out from speed.</summary>
    public float BrakingDistance { get; set; } = 700f;

    /// <summary>Seconds of travel to spend braking. The zone is this times the ship's top speed,
    /// so a fast ship gets room to stop and a slow one doesn't crawl for a minute.</summary>
    public float BrakingSeconds { get; set; } = 1.6f;

    /// <summary>Floor on the braking zone, for ships slow enough that the seconds figure would
    /// leave no room to shed speed at all.</summary>
    public float MinBrakeDistance { get; set; } = 120f;

    /// <summary>Slowest we'll crawl in on the final approach.</summary>
    public float MinApproachSpeed { get; set; } = 8f;

    /// <summary>
    /// Steer around solid objects that are not the thing we're flying to.
    ///
    /// Every heading the bot sends points straight at its target, and the braking ramp is
    /// worked out from the distance to that target alone — so nothing in between was ever
    /// considered. The case that kills is re-targeting: a rock scans empty, stops being a
    /// candidate, and the next tick picks a rock somewhere else and opens the throttle while
    /// the abandoned one is still directly ahead at close range.
    /// </summary>
    public bool AvoidCollisions { get; set; } = true;

    /// <summary>Clearance to add to an obstacle's own radius, in units. The published radius is
    /// a bounding figure for the object, and says nothing about our own hull.</summary>
    public float CollisionMargin { get; set; } = 130f;

    /// <summary>How far ahead to look for obstacles, in seconds of travel at top speed. This has
    /// to cover the turn as well as the stop: the heading only updates a few times a second, so
    /// a deflection decided one ship-length out never lands.</summary>
    public float CollisionLookaheadSeconds { get; set; } = 5f;

    /// <summary>
    /// Wait for a weapon's own optimal range before firing it, while still flying in. A cannon
    /// quoted at 250u but reaching 600u otherwise empties the power pool on long shots during
    /// every approach. Ignored once we've stopped closing — see <see cref="FireAll"/>.
    /// </summary>
    public bool HoldFireUntilOptimal { get; set; } = true;

    /// <summary>
    /// Shoot rocks with your combat guns as well as your mining laser. Cannons damage asteroids
    /// perfectly well, so the only reason to turn this off is to keep the power pool for the
    /// laser and the scanner. Positioning is unaffected — the laser still sets the standoff.
    /// </summary>
    public bool FireGunsWhileMining { get; set; } = true;

    /// <summary>
    /// Only scan when the answer would change what we do — i.e. when a resource filter is set.
    /// A Mineral Analysis Module costs 50 power at level 1 against a 100-point pool, which is
    /// several seconds of mining lasers. Paying that for information nobody reads is the
    /// difference between farming and drifting with a flat battery.
    /// </summary>
    public bool ScanOnlyWhenFiltering { get; set; } = true;

    /// <summary>Fraction of the power pool to keep back for weapons when deciding to scan.</summary>
    public float ScanPowerReserve { get; set; } = 0.25f;

    /// <summary>Cap on ids in one area cast. The client sends everything in range with no limit,
    /// but a sane ceiling keeps one message from getting absurd in a crowded belt.</summary>
    public int MaxAreaScanTargets { get; set; } = 32;

    /// <summary>Cadence for the scan sweep when the scanner ability has no cooldown stat.</summary>
    public int ScanIntervalMs { get; set; } = 1200;

    /// <summary>How long to wait for a scan reply before asking about that rock again.</summary>
    public int ScanRetrySeconds { get; set; } = 20;

    /// <summary>
    /// How long a scan result is trusted before the rock is worth re-scanning. Asteroid resources
    /// respawn on a server-side timer and can come back as something else.
    ///
    /// Three minutes was less than it takes to break one rock open, so by the time the bot looked
    /// up from the first target it had forgotten the other four it had already paid to identify
    /// and started the sweep again. A scan is only wrong if the rock respawns, which needs it to
    /// be emptied first — so trusting one for a working session is the cheaper mistake.
    /// </summary>
    public int ScanFreshnessSeconds { get; set; } = 900;

    /// <summary>
    /// How many confirmed rocks to keep queued up. At or above this, the sweep stops: power is
    /// worth more in the lasers than in identifying a rock we won't get to for minutes.
    /// </summary>
    public int ScanQueueDepth { get; set; } = 2;

    /// <summary>Spacing between scanner-identification probes, so a ship full of utility
    /// slots doesn't fire all of them in one tick.</summary>
    public int ProbeIntervalSeconds { get; set; } = 3;

    // ---- counters -----------------------------------------------------------------
    public int Kills { get; private set; }
    public int ShotsFired { get; private set; }
    public int LootTaken { get; private set; }
    public int ScansSent { get; private set; }
    public int RepairsCast { get; private set; }

    /// <summary>Times the ship was on a collision course and steered off it.</summary>
    public int NearMisses { get; private set; }

    /// <summary>Measured throughput — regen, ore per hour, and where the time actually goes.
    /// See <see cref="MiningMeter"/> for why none of this can be read off an item card.</summary>
    public MiningMeter Meter { get; } = new();

    /// <summary>Most recent moment any weapon fired, so the meter can discard contaminated
    /// power samples.</summary>
    private DateTime _lastAnyShot = DateTime.MinValue;
    public int Rejections { get; private set; }
    public string Status { get; private set; } = "Idle";
    public uint CurrentTarget => _target;

    /// <summary>The contact you pinned by hand, or 0.</summary>
    public uint PinnedTarget { get { lock (_gate) return _pinned; } }

    public event Action<string>? Log;

    /// <summary>
    /// Raised for every ability id seen leaving the real client, whether or not it was new.
    ///
    /// This is what makes "press the key and I'll bind it" possible: you fire the thing in
    /// game, the id goes past on the wire, and the loadout panel now knows which slot the hex
    /// you were editing actually is. No amount of stat sniffing can establish that mapping,
    /// because nothing on the wire ties a slot id to a position in the game's own UI.
    /// </summary>
    public event Action<ushort>? AbilitySeen;

    public FarmBot(WorldState world, GameActions actions, GameProxy proxy)
    {
        _world = world;
        _act = actions;
        _proxy = proxy;

        proxy.Frame += OnFrame;
        proxy.SessionStarted += OnSessionStarted;
        proxy.SessionEnded += OnSessionEnded;

        _world.Died += OnObjectDied;
        _world.LootOffered += OnLootOffered;
        _world.CastResult += OnCastResult;
        _world.AbilityStopped += OnAbilityStopped;
        _world.SectorLeft += OnSectorLeft;
        _world.ScanReceived += OnScanReceived;
        _world.HoldGained += items => Meter.OnHoldGained(items, DateTime.UtcNow);

        Weapons.Learned += (w, isNew) =>
        {
            if (isNew) Log?.Invoke($"Learned weapon {w.Describe()}");
        };

        _timer = new System.Threading.Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start()
    {
        // A fly-to run would otherwise swallow every tick and farming would silently never
        // happen. Starting the farm is an instruction to stop doing the other thing.
        if (_following)
        {
            _following = false;
            _followTarget = 0;
            Log?.Invoke("Fly-to ended — farming takes the ship back.");
        }

        Enabled = true;
        Status = "Starting";
        _timer.Change(0, 250);
        Log?.Invoke("Farm started.");
    }

    public void Stop()
    {
        Enabled = false;
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        _ = DisengageAsync("farm stopped");
        Status = "Idle";
        Log?.Invoke("Farm stopped.");
    }

    // ------------------------------------------------------------------ traffic

    private void OnSessionStarted()
    {
        _world.Clear();
        Weapons.ResetToggles();
        ForgetThrottle();
        _following = false;
        _followTarget = 0;
        lock (_gate)
        {
            _target = 0; _lockedTarget = 0; _subscribedTarget = 0; _pinned = 0;
            _lootAsked.Clear(); _facilityOrdered.Clear(); _skip.Clear();
            _scanAsked.Clear(); _scanProbe.Clear(); _probed.Clear();
        }
        _scansWithoutReply = 0;
        _ammoWarned = false;
    }

    private void OnSessionEnded()
    {
        Weapons.ResetToggles();
        lock (_gate) { _target = 0; _lockedTarget = 0; _subscribedTarget = 0; }
    }

    private void OnSectorLeft(RemovingCause cause)
    {
        Weapons.ResetToggles();
        ForgetThrottle();

        // Leaving by Dock means the dock run worked; anything else ends it just as surely.
        if (_docking)
        {
            _docking = false;
            _dockTarget = 0;
            Status = cause == RemovingCause.Dock ? "Docked" : $"Dock run ended ({cause})";
            if (!Enabled) _timer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        // A destination in a sector you are no longer in is not a destination.
        if (_following) EndFollow($"Fly-to ended — you left the sector ({cause})");
        lock (_gate)
        {
            _target = 0; _lockedTarget = 0; _subscribedTarget = 0; _pinned = 0;
            _lootAsked.Clear(); _facilityOrdered.Clear();
            // Rocks are per-sector; the learned scanner is not, so _probed stays.
            _scanAsked.Clear(); _scanProbe.Clear();
        }
        Log?.Invoke($"Left the sector ({cause}). World cleared.");
    }

    /// <summary>Watches both directions: your traffic teaches the bot, the server's builds the map.</summary>
    private void OnFrame(FrameInfo f)
    {
        try
        {
            var r = f.Reader();

            if (!f.FromClient)
            {
                _world.OnServerMessage(f.Protocol, f.MsgType, r);
                return;
            }

            switch (f.Protocol)
            {
                case ProtocolId.Login when (LoginOp.Request)f.MsgType == LoginOp.Request.Player:
                {
                    r.ReadByte();                                  // ConnectType
                    _world.SeedPlayerId(r.ReadUInt32(), "your login");
                    break;
                }

                case ProtocolId.Game:
                    OnClientGameMessage(f.MsgType, r);
                    break;
            }
        }
        catch
        {
            // Short or unfamiliar payloads are normal — never let parsing break the relay.
        }
    }

    private void OnClientGameMessage(ushort msgType, BgoReader r)
    {
        switch ((GameOp.Request)msgType)
        {
            // The three ways the client fires. Only watching CastSlotAbility meant a beam or
            // any auto-cast weapon never registered, no matter how long you held the trigger.
            // What you aimed at is read too: an ability pointed at a rock is a mining laser,
            // one pointed at a ship is a gun. Assuming "combat" for everything you fired is
            // what left mining mode forever asking you to fire your laser once manually.
            case GameOp.Request.CastSlotAbility:
            case GameOp.Request.CastImmutableSlotAbility:
            {
                ushort id = r.ReadUInt16();
                var targets = ReadTargets(r);
                Weapons.Observe(id, WeaponKind.Cast, RoleOf(targets));
                LearnAreaEffect(id, targets);
                NoteScanProbe(id, targets);
                AbilitySeen?.Invoke(id);
                break;
            }

            case GameOp.Request.ToggleAbilityOn:
            case GameOp.Request.UpdateAbilityTargets:
            {
                ushort id = r.ReadUInt16();
                var targets = ReadTargets(r);
                Weapons.Observe(id, WeaponKind.Toggle, RoleOf(targets));
                LearnAreaEffect(id, targets);
                NoteScanProbe(id, targets);
                AbilitySeen?.Invoke(id);
                break;
            }

            // The client has already turned Full/Delta into an absolute number by the time it
            // gets here, so your own throttle is a server-independent source for our top speed.
            case GameOp.Request.SetSpeed:
            {
                r.ReadByte();                                  // SpeedMode — no server reads it
                float v = r.ReadSingle();
                // Only worth a line when it actually moves the number we fly at — otherwise
                // every tap of your throttle key logged a "learned" speed that changed nothing.
                if (v > _observedTopSpeed)
                {
                    float before = TopSpeed;
                    _observedTopSpeed = v;
                    if (TopSpeed > before)
                        Log?.Invoke($"Watched you fly at {v:F0}u/s — using that as the top speed.");
                }
                break;
            }

            case GameOp.Request.ToggleAbilityOff:
            {
                ushort id = r.ReadUInt16();
                var w = Weapons.Find(id);
                if (w is not null) { w.ToggledOn = false; w.ToggleTarget = 0; }
                break;
            }

            // You picked a target by hand — respect it if it suits the current mode.
            case GameOp.Request.LockTarget:
            {
                uint id = r.ReadUInt32();
                AdoptManualTarget(id);
                break;
            }

            // You asked to mine something by hand — take that as the mining target.
            case GameOp.Request.Mining:
                AdoptManualTarget(r.ReadUInt32());
                break;

            // You docked by hand. The distance you did it from is a proven-good dock range for
            // that station, which beats any number we could invent.
            case GameOp.Request.Dock:
            {
                uint id = r.ReadUInt32();
                var station = _world.Get(id);
                if (station is not null && _world.DistanceToMe(station) is { } d && d > _learnedDockRange)
                {
                    _learnedDockRange = d;
                    Log?.Invoke($"Learned a working dock range: {d:F0}u (from your own docking).");
                }
                break;
            }
        }
    }

    /// <summary>The id list that follows an ability id in every cast and toggle message.</summary>
    private static List<uint> ReadTargets(BgoReader r)
    {
        var ids = new List<uint>(1);
        try
        {
            int n = r.ReadUInt16();
            for (int i = 0; i < n; i++) ids.Add(r.ReadUInt32());
        }
        catch
        {
            // Truncated list — whatever we got is still usable.
        }
        return ids;
    }

    /// <summary>
    /// What an ability is for, judged by what you aimed it at. Weak evidence only: the
    /// per-slot stat stream is authoritative and overwrites this.
    /// </summary>
    private WeaponRole RoleOf(List<uint> targets)
    {
        foreach (uint id in targets)
        {
            // Your own ship first. Damage Control and every other self-cast targets you, and you
            // are ship-shaped — so this used to come back Combat, and the bot would happily try
            // to shoot an NPC with your repair module.
            if (id != 0 && id == _world.MyObjectId) return WeaponRole.Repair;
            if (EntityTypes.IsMinable(id)) return WeaponRole.Mining;
            if (EntityTypes.IsShip(id)) return WeaponRole.Combat;
        }
        return WeaponRole.Unknown;
    }

    /// <summary>
    /// Remembers that you pointed an ability at a rock. If the server answers with a scan for
    /// that same rock in the next few seconds, that ability was the scanner — which is the only
    /// way to find it without parsing the catalogue, since nothing else names it on the wire.
    /// </summary>
    /// <summary>
    /// Works out whether an ability is area-effect purely from watching you use it, because the
    /// consequence of getting it wrong is a cheat entry in the server log.
    ///
    /// The client's own rule makes this decidable: an Area cast carries EVERY valid object in
    /// range, a Selected cast carries exactly one. So more than one id proves Area outright, and
    /// exactly one id while several valid targets were in range proves Selected.
    /// </summary>
    private void LearnAreaEffect(ushort abilityId, List<uint> targets)
    {
        var w = Weapons.Find(abilityId);
        if (w is null || w.Area is not null) return;

        if (targets.Count > 1)
        {
            w.Area = true;
            Log?.Invoke($"Ability #{abilityId} is area-effect ({targets.Count} targets in one cast).");
            return;
        }

        if (targets.Count != 1) return;

        float reach = _world.SlotStat(abilityId, ObjectStat.MaxRange) ?? w.MaxRange ?? 0f;
        if (reach <= 0f) return;

        var now = DateTime.UtcNow;
        int inRange = _world.Snapshot().Count(o => EntityTypes.IsMinable(o.Id)
                                               && o.HasPosition
                                               && (_world.DistanceToMe(o) ?? float.MaxValue) <= reach);
        if (inRange >= 2)
        {
            w.Area = false;
            Log?.Invoke($"Ability #{abilityId} is single-target ({inRange} rocks were in reach, it took one).");
        }
    }

    private void NoteScanProbe(ushort abilityId, List<uint> targets)
    {
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            foreach (uint id in targets)
                if (EntityTypes.IsMinable(id)) _scanProbe[id] = (abilityId, now);

            // Never let this grow: anything older than the correlation window is dead weight.
            if (_scanProbe.Count > 64)
                foreach (var stale in _scanProbe.Where(p => (now - p.Value.At).TotalSeconds > 10).Select(p => p.Key).ToList())
                    _scanProbe.Remove(stale);
        }
    }

    private void OnScanReceived(uint asteroidId)
    {
        // Any reply at all proves the scanner is fed and firing, whoever triggered it.
        _scansWithoutReply = 0;

        ushort ability;
        lock (_gate)
        {
            if (!_scanProbe.TryGetValue(asteroidId, out var probe)) return;
            if ((DateTime.UtcNow - probe.At).TotalSeconds > 5) { _scanProbe.Remove(asteroidId); return; }
            _scanProbe.Remove(asteroidId);
            ability = probe.Ability;
        }

        var w = Weapons.MarkScanner(ability);
        if (w is not null)
            Log?.Invoke($"Learned your resource scanner: ability #{ability}. Mining can filter by resource now.");
    }

    // ------------------------------------------------------------------ picked by hand

    /// <summary>
    /// Holds one contact as the target until it dies, leaves the sector, or you clear it.
    ///
    /// Distinct from the target the bot picks: that one is re-derived every tick from whatever
    /// is nearest and eligible, so writing to it would last exactly one tick. A pin is checked
    /// before the hunting rules and survives them — including the "attack players" and prey
    /// filters, because pointing at something explicitly is a clearer instruction than any
    /// checkbox.
    /// </summary>
    public void Pin(uint id)
    {
        if (id == 0 || id == _world.MyObjectId) return;
        lock (_gate)
        {
            _pinned = id;
            _target = id;
            _lockedTarget = 0;              // force a fresh LockTarget on the next tick
            _skip.Remove(id);
        }

        var o = _world.Get(id);
        bool suits = Mode == FarmMode.Mining ? EntityTypes.IsMinable(id) : !EntityTypes.IsMinable(id);
        Log?.Invoke($"Pinned {o?.ToString() ?? $"#{id:X8}"} as the target."
                  + (suits ? "" : $" It is not a {Mode} target, so switch mode or it will be dropped."));
    }

    public void Unpin()
    {
        bool had;
        lock (_gate) { had = _pinned != 0; _pinned = 0; }
        if (had) Log?.Invoke("Pin cleared — back to picking targets automatically.");
    }

    /// <summary>
    /// Fires one ability once, so you can see in game which slot an id belongs to.
    ///
    /// Aimed at whatever is locked, falling back to your own ship: a repair module cast at a
    /// rock is refused, and a gun cast at yourself is refused, but between the two every slot
    /// gets a shot at showing itself. The server may well reject it — that is fine, the point
    /// is the visible cooldown sweep on the hex in the real client.
    /// </summary>
    public async Task TestFireAsync(ushort abilityId)
    {
        uint at;
        lock (_gate) at = _target;
        if (at == 0) at = _world.MyObjectId;

        var w = Weapons.Find(abilityId);
        string aimed = at == _world.MyObjectId ? "at your own ship" : $"at #{at:X8}";
        try
        {
            if (w?.Kind == WeaponKind.Toggle) await _act.ToggleAbilityOn(abilityId, at);
            else await _act.CastSlotAbility(abilityId, at);
            Log?.Invoke($"Test fired ability #{abilityId} {aimed} — watch which hex lights up in game.");
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Could not test fire #{abilityId}: {ex.Message}");
        }
    }

    private void AdoptManualTarget(uint id)
    {
        if (id == 0 || id == _world.MyObjectId) return;
        bool suits = Mode == FarmMode.Mining ? EntityTypes.IsMinable(id) : EntityTypes.IsShip(id);
        if (!suits) return;

        lock (_gate)
        {
            if (_target == id) return;
            _target = id;
            _lockedTarget = id;      // the client just sent the lock; no need to repeat it
            _skip.Remove(id);
        }
        Log?.Invoke($"Following your manual target #{id:X8}.");
    }

    private void OnObjectDied(uint id)
    {
        bool wasTarget;
        lock (_gate)
        {
            wasTarget = id == _target;
            if (wasTarget) { _target = 0; _lockedTarget = 0; }
            if (id == _pinned) _pinned = 0;
            _skip.Remove(id);
        }

        if (!wasTarget) return;

        Kills++;
        Log?.Invoke($"Target #{id:X8} destroyed (kill {Kills}).");
        _ = StopAllTogglesAsync();
        if (AutoLoot) _ = TryLootAsync(id);
    }

    private void OnCastResult(ushort slot, bool ok)
    {
        if (ok) return;
        Rejections++;
        // Rate-limited: a rejected cast every tick would flood the log.
        if (Rejections % 20 == 1)
            Log?.Invoke($"Server rejected ability #{slot} (out of range, no power, or on cooldown).");
    }

    private void OnAbilityStopped(short slot)
    {
        if (slot < 0) { Weapons.ResetToggles(); return; }
        var w = Weapons.Find((ushort)slot);
        if (w is not null) { w.ToggledOn = false; w.ToggleTarget = 0; }
    }

    private void OnLootOffered(ushort lootId, IReadOnlyList<LootItem> items)
    {
        if (!AutoLoot || items.Count == 0) return;
        var ids = items.Select(i => i.ServerId).ToList();
        _ = _act.TakeLootItems(lootId, ids);
        LootTaken += ids.Count;
        Log?.Invoke($"Taking {ids.Count} item(s) from loot #{lootId}.");
    }

    // ------------------------------------------------------------------ loop

    private void Tick()
    {
        if (!Enabled && !_docking && !_following) return;
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;   // never overlap ticks
        try
        {
            TickCore().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Status = "Error: " + ex.Message;
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private async Task TickCore()
    {
        // Sample the pool every tick, whatever else happens — the meter throws away the
        // intervals it can't trust, and a regen figure needs the quiet moments most.
        var sampledAt = DateTime.UtcNow;
        Meter.OnPower(_world.MyPower, _world.MyMaxPower, sampledAt, LastPowerSpend(sampledAt));

        if (!_proxy.ClientConnected)
        {
            Status = "Waiting for the game client to connect";
            return;
        }

        // Cheap, but no point doing it every 250 ms.
        if ((DateTime.UtcNow - _lastStatsSweep).TotalSeconds > 2)
        {
            _lastStatsSweep = DateTime.UtcNow;
            Weapons.RefreshFromStats(_world);
        }

        if (_world.MyObjectId == 0)
        {
            Status = _world.MyPlayerId == 0
                ? "Don't know who you are yet — waiting for the login handshake"
                : $"Waiting for your ship's WhoIs (player {_world.MyPlayerId}). Undock or jump in.";
            return;
        }

        if (!_world.MyPositionKnown)
        {
            Status = "Know your ship, but the server hasn't sent its position yet";
            return;
        }

        // Compare ratios with ratios. MyHull is in points, so the old `MyHull < RetreatHull`
        // asked whether 495 was below 0.25 — it never was, and the retreat threshold did nothing.
        // A dock run owns the ship while it lasts — no targeting, no firing, no retreat logic.
        if (_docking)
        {
            await DockTick();
            return;
        }

        // Patch the hull before deciding anything else — a repair that lands now may be the
        // difference between fighting on and running.
        await SelfRepairAsync();

        if (_world.MyHullFraction is { } hull && hull < RetreatHull)
        {
            if (FleeWhenHurt) { await FleeTick(hull); return; }
            await DisengageAsync($"hull at {hull:P0}");
            Status = $"HULL {hull:P0} — disengaged. Raise the retreat threshold to keep going.";
            return;
        }

        // Sitting inside an enemy station's envelope outranks farming: nothing found there is
        // worth what it costs, and the station will keep firing for as long as we stay.
        if (StationTooClose() is { } danger)
        {
            await LeaveStationDangerAsync(danger);
            return;
        }

        // Below the hull and station guards, above everything else: a run you started by hand
        // owns the ship, but not so completely that it flies you into an outpost or refuses to
        // patch the hull on the way.
        if (_following)
        {
            await FollowTick();
            return;
        }

        if (AutoLoot) await SweepLootAsync();

        if (Mode == FarmMode.Mining)
        {
            // Mining is what we're here for, but not while something is shooting. The guns are
            // already fitted; the bot simply wasn't looking up from the rock.
            if (DefendSelf && NearestThreat() is not null)
            {
                await CombatTick(IsThreat, "Defending");
                return;
            }
            await MineTick();
        }
        else await CombatTick();
    }

    // ------------------------------------------------------------------ fly to / follow

    /// <summary>Where to stop, in units from a contact's centre, on a fly-to or follow run.</summary>
    public float FollowDistance { get; set; } = 350f;

    /// <summary>
    /// Give up a one-shot <b>Go to</b> that has made no ground for this long.
    ///
    /// Only a Go to. A <b>Follow</b> never gives up, however far behind it falls: something that
    /// is outrunning you now can turn, stop, dock or lose its boost a minute later, and a chase
    /// that quits the moment the gap widens is a chase that never catches anything. It says it is
    /// losing ground and keeps flying.
    /// </summary>
    public int FollowStallSeconds { get; set; } = 30;

    public bool IsFollowing => _following;

    /// <summary>True while the run is keeping station rather than just arriving.</summary>
    public bool IsHoldingStation => _following && _followHold;

    public uint FollowTarget => _followTarget;

    /// <summary>
    /// Fly to a contact you picked out of the list.
    ///
    /// Two shapes, one run. <paramref name="keepStation"/> false is a fly-to: it ends the moment
    /// the ship arrives. True is a follow: it holds the standoff for as long as you leave it on,
    /// re-closing whenever the contact pulls away. Both take the ship over completely — farming
    /// stops, because two things cannot steer at once and pretending otherwise just produces a
    /// ship that jitters between two destinations.
    /// </summary>
    public void FlyTo(uint id, bool keepStation)
    {
        if (id == 0 || id == _world.MyObjectId) return;

        var target = _world.Get(id);
        if (target is null || !target.HasPosition)
        {
            Status = "Can't fly there — that contact has no position yet";
            Log?.Invoke($"Fly-to #{id:X8} refused: the server has never said where it is. "
                      + "Ask WhoIs, or wait for it to move.");
            return;
        }

        if (_docking) CancelDock();
        if (Enabled)
        {
            Stop();
            Log?.Invoke("Farming stopped — a fly-to run steers the ship itself.");
        }

        _followTarget = id;
        _following = true;
        _followHold = keepStation;
        _followBest = float.MaxValue;
        _followProgress = DateTime.UtcNow;
        _followLosingGround = false;
        ForgetThrottle();

        float hold = FollowStandoff(target);
        Log?.Invoke(keepStation
            ? $"Following {target} at {hold:F0}u."
            : $"Flying to {target} — {_world.DistanceToMe(target):F0}u away, stopping at {hold:F0}u.");

        _timer.Change(0, 250);              // the run needs the loop even with farming off
    }

    public void StopFollowing()
    {
        if (!_following) return;
        EndFollow("Stopped");
        Log?.Invoke("Fly-to cancelled.");
        _ = StopThrottleIfMoving();
    }

    /// <summary>Ends the run and parks the timer if nothing else still needs it.</summary>
    private void EndFollow(string status)
    {
        _following = false;
        _followTarget = 0;
        Status = status;
        if (!Enabled && !_docking) _timer.Change(Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>
    /// Where to sit. No weapons are involved in a fly-to, so this is only about not flying into
    /// the thing: <see cref="FollowDistance"/>, floored by the target's own size, because every
    /// distance on the wire is centre-to-centre and a planetoid is not a point.
    /// </summary>
    private float FollowStandoff(SpaceObj target)
    {
        float clear = target.Radius > 0
            ? target.Radius * RadiusClearance + MinimumStandoff
            : MinimumStandoff;
        return Math.Max(FollowDistance, clear);
    }

    private async Task FollowTick()
    {
        var target = _world.Get(_followTarget);
        if (target is null || !target.HasPosition)
        {
            Log?.Invoke($"Fly-to ended — #{_followTarget:X8} left the sector.");
            EndFollow("Lost the contact — it left the sector");
            await StopThrottleIfMoving();
            return;
        }

        var now = DateTime.UtcNow;
        float dist = _world.DistanceToMe(target) ?? float.MaxValue;
        float hold = FollowStandoff(target);

        // Hysteresis on the way out, or the ship pumps the throttle on and off across the hold
        // line every time the contact drifts a metre.
        float slack = Math.Max(60f, hold * 0.25f);

        if (dist <= hold + slack)
        {
            await StopThrottleIfMoving();
            _followBest = float.MaxValue;         // arriving resets the stall clock
            _followProgress = now;

            if (!_followHold)
            {
                Log?.Invoke($"Arrived at {target} ({dist:F0}u).");
                EndFollow($"Arrived at {target} — {dist:F0}u");
                return;
            }

            Status = $"Holding {hold:F0}u off {target} — {dist:F0}u"
                   + (target.Velocity.LengthSquared() > 1f ? ", it's moving" : "");
            return;
        }

        // Are we gaining or losing? Reported either way, but only a Go to is allowed to end on
        // it. A Follow that quits because the gap widened would drop every chase worth having:
        // the ship pulling away now is the one that turns, stops or docks in a minute, and the
        // only way to be there when it does is to still be behind it.
        bool gaining = dist < _followBest - 1f;
        if (gaining) { _followBest = dist; _followProgress = now; }

        double stalled = (now - _followProgress).TotalSeconds;

        if (!_followHold && stalled > FollowStallSeconds)
        {
            Log?.Invoke($"Gave up flying to {target} — no ground made in {FollowStallSeconds}s at "
                      + $"{dist:F0}u. Use Follow if you want the bot to keep after it.");
            EndFollow($"Can't reach {target} — held at {dist:F0}u");
            await StopThrottleIfMoving();
            return;
        }

        // Said once, not every tick, and only after it has clearly stopped being a blip.
        if (_followHold && !gaining && stalled > FollowStallSeconds && !_followLosingGround)
        {
            _followLosingGround = true;
            Log?.Invoke($"{target} is outrunning you — still chasing at {dist:F0}u.");
        }
        else if (gaining) _followLosingGround = false;

        // The shared approach watchdog is switched off here: it exists to make the farm loop
        // give up on an unreachable target and pick another, and it does that by skipping the
        // object for two minutes. On a run you asked for by hand, the stall check above is the
        // one that should end it, with a message that says what actually happened.
        await SteerToward(target, hold, watchdog: false);

        string how = _followHold
            ? _followLosingGround ? "Chasing (losing ground)" : "Following"
            : "Flying to";
        Status = $"{how} {target} — {dist:F0}u, stopping at {hold:F0}u, {_throttle:F0}u/s {_gear}";
    }

    // ------------------------------------------------------------------ docking

    /// <summary>How close to get before asking to dock, when nothing has been learned yet.</summary>
    public float DockApproach { get; set; } = 250f;

    /// <summary>Give up on a dock run after this long.</summary>
    public int DockTimeoutSeconds { get; set; } = 90;

    public bool IsDocking => _docking;

    /// <summary>
    /// Fly to the nearest station and dock. Stops farming first — you don't want the bot
    /// opening fire on the way in.
    /// </summary>
    public void BeginDock()
    {
        if (_following) StopFollowing();
        if (Enabled) Stop();

        var station = _world.Nearest(o => EntityTypes.IsDockable(o.Id)
                                       && _world.RelationTo(o.Id) is Relation.Friend or Relation.Neutral or Relation.Self);
        if (station is null)
        {
            Status = "Nothing dockable in range — no outpost or capital ship located";
            Log?.Invoke("Dock: no dockable object located in this sector.");
            return;
        }

        _dockTarget = station.Id;
        _docking = true;
        _dockAsked = DateTime.MinValue;
        _dockStarted = DateTime.UtcNow;
        ForgetThrottle();

        Log?.Invoke($"Docking at {station} ({_world.DistanceToMe(station):F0}u away).");
        _timer.Change(0, 250);              // the dock run needs the loop even with farming off
    }

    public void CancelDock()
    {
        if (!_docking) return;
        _docking = false;
        _dockTarget = 0;
        _ = _act.CancelDocking();
        _ = StopThrottleIfMoving();
        Status = "Docking cancelled";
        Log?.Invoke("Docking cancelled.");
        if (!Enabled) _timer.Change(Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Launch back into the sector. One message, no approach needed.</summary>
    public void Undock()
    {
        _docking = false;
        _dockTarget = 0;
        _ = _act.JumpIn();
        Status = "Undocking";
        Log?.Invoke("Undock requested (JumpIn).");
    }

    private async Task DockTick()
    {
        var station = _world.Get(_dockTarget);
        if (station is null || !station.HasPosition)
        {
            Status = "Lost the station — it left the sector or was never located";
            _docking = false;
            return;
        }

        if ((DateTime.UtcNow - _dockStarted).TotalSeconds > DockTimeoutSeconds)
        {
            Status = "Gave up docking — took too long";
            Log?.Invoke("Dock run timed out.");
            _docking = false;
            await StopThrottleIfMoving();
            return;
        }

        float dist = _world.DistanceToMe(station) ?? float.MaxValue;

        float ask = DockRange(station);

        if (dist > ask)
        {
            await SteerToward(station, ask);
            Status = $"Docking — {dist:F0}u to {station}, closing to {ask:F0}u, {_throttle:F0}u/s {_gear}";
            return;
        }

        await StopThrottleIfMoving();

        // Once per few seconds, not per tick: every rejected attempt writes a line in the
        // server log with your player id.
        if ((DateTime.UtcNow - _dockAsked).TotalSeconds < 4) return;
        _dockAsked = DateTime.UtcNow;

        await _act.Dock(station.Id);
        Status = $"Dock requested at {station} from {dist:F0}u";
        Log?.Invoke($"Dock requested at #{station.Id:X8} from {dist:F0}u.");
    }

    // ------------------------------------------------------------------ combat

    /// <summary>
    /// Shoot things. <paramref name="candidate"/> defaults to the normal hunting rules; the
    /// mining loop passes <see cref="IsThreat"/> so self-defence reuses all of this — approach,
    /// standoff, cooldowns — instead of growing a second, worse copy of it.
    /// </summary>
    private async Task CombatTick(Func<SpaceObj, bool>? candidate = null, string verb = "Attacking")
    {
        // A caller-supplied candidate means this is the self-defence path, not the hunt — a pin
        // must not override "something is shooting at me".
        bool hunting = candidate is null;
        candidate ??= CombatCandidate;

        var guns = Weapons.For(WeaponRole.Combat);
        if (guns.Count == 0)
        {
            Status = "No weapon known yet — fire once manually, or wait for the server's slot stats";
            return;
        }

        float range = EffectiveRange(guns);
        var target = ResolveTarget(candidate, honourPin: hunting);

        if (target is null)
        {
            await StopAllTogglesAsync();
            int seen = _world.Snapshot().Count(candidate);
            Status = seen == 0
                ? "No hostiles in the sector"
                : $"{seen} hostile(s) known but none located yet";
            return;
        }

        float dist = _world.DistanceToMe(target) ?? float.MaxValue;
        float preferred = StandoffFor(target, guns);

        if (dist > range)
        {
            if (AutoApproach)
            {
                await SteerToward(target, preferred);
                Status = $"Closing on {target} — {dist:F0}u, want {preferred:F0}u"
                       + $", {_throttle:F0}u/s {_gear}";
            }
            else
            {
                await StopAllTogglesAsync();
                Status = $"{target} is {dist:F0}u away, weapons reach {range:F0}u (auto-approach off)";
            }
            return;
        }

        // In reach — but keep flying in until we're inside the accurate band. Opening fire at
        // the edge of max range and stopping there is how a viper misses every shot.
        bool closing = AutoApproach && dist > preferred;
        if (closing) await SteerToward(target, preferred);
        else await StopThrottleIfMoving();

        await EnsureLocked(target.Id);
        await EnsureSubscribed(target.Id);

        int fired = await FireAll(guns, target, dist, closing);
        Status = $"{verb} {target} — {dist:F0}u / {range:F0}u"
               + (target.StatsKnown ? $", hull {target.Hull:F0}" : "")
               + (fired > 0 ? $", {fired} weapon(s) firing" : ", holding (cooldown)")
               + (closing ? $", closing to {preferred:F0}u" : "");
    }

    /// <summary>
    /// Something worth pointing guns at right now, regardless of what we were doing. Anything
    /// that has locked us is a threat at any distance — it is already shooting. Anything else
    /// has to be inside <see cref="ThreatRange"/> to count.
    /// </summary>
    private bool IsThreat(SpaceObj o)
    {
        if (!IsHostile(o)) return false;

        // Emplacements are left out of the *return fire* set on purpose: soloing an outpost is a
        // death sentence, not a fight. Running from one is still the right move, which is what
        // IsDanger is for.
        if (IsEmplacement(o)) return false;

        if (o.TargetId == _world.MyObjectId) return true;
        if (!EntityTypes.IsNpcCombatant(o.Id) && !IsHomingHazard(o)) return false;
        return (_world.DistanceToMe(o) ?? float.MaxValue) <= ThreatRange;
    }

    /// <summary>
    /// Anything worth running from. Wider than <see cref="IsThreat"/>: it includes stations and
    /// anything else that has locked us, because "I can't shoot that" is not a reason to sit
    /// still while it shoots us.
    /// </summary>
    private bool IsDanger(SpaceObj o)
    {
        if (!IsHostile(o)) return false;
        if (o.TargetId == _world.MyObjectId) return true;
        if (!EntityTypes.IsNpcCombatant(o.Id) && !IsHomingHazard(o)) return false;
        return (_world.DistanceToMe(o) ?? float.MaxValue) <= ThreatRange;
    }

    /// <summary>
    /// The shape test both threat sets share. Deliberately *not* <c>IsShip</c>: a seeker drone or
    /// a homing mine is not a ship by object type, so gating on IsShip made one invisible to the
    /// bot even while it was locked on and firing. Anything that isn't scenery can hurt us.
    /// </summary>
    private bool IsHostile(SpaceObj o)
    {
        if (o.IsMe || o.Id == _world.MyObjectId) return false;
        if (o.Cloaked) return false;
        if (EntityTypes.IsStatic(o.Id) || EntityTypes.IsMinable(o.Id)) return false;
        return _world.RelationTo(o.Id) is Relation.Enemy or Relation.Neutral;
    }

    /// <summary>Mines chase and detonate. They never appear in an NPC-combatant list.</summary>
    private static bool IsHomingHazard(SpaceObj o) =>
        o.Type is SpaceEntityType.Mine or SpaceEntityType.SmartMine or SpaceEntityType.MineField;

    private SpaceObj? NearestThreat() => _world.Nearest(IsThreat);

    private SpaceObj? NearestDanger() => _world.Nearest(IsDanger);

    private bool CombatCandidate(SpaceObj o)
    {
        if (o.IsMe || o.Id == _world.MyObjectId) return false;
        if (o.Cloaked) return false;
        if (IsSkipped(o.Id)) return false;

        // IsShip includes outposts and weapon platforms. Ticking "attack players" therefore used
        // to sign the ship up to solo an enemy station, which is a death sentence rather than a
        // fight — unless they're in the prey list, in which case you asked for it.
        bool shape = AttackPlayers ? EntityTypes.IsShip(o.Id) : EntityTypes.IsNpcCombatant(o.Id);
        if (!shape) return false;
        if (IsEmplacement(o) && !Prey.Contains(o.Type)) return false;

        // An explicit prey list wins, except that players are governed by AttackPlayers alone.
        if (Prey.Count > 0 && o.Type != SpaceEntityType.Player && !Prey.Contains(o.Type)) return false;

        // Nothing inside a station's envelope is worth it: killing the NPC there means taking the
        // station's fire for the whole fight.
        if (InStationDanger(o)) return false;

        return _world.RelationTo(o.Id) is Relation.Enemy or Relation.Neutral;
    }

    // ------------------------------------------------------------------ hostile emplacements

    /// <summary>A gun that doesn't move: weapon platform or outpost.</summary>
    private static bool IsEmplacement(SpaceObj o) =>
        o.Type is SpaceEntityType.WeaponPlatform or SpaceEntityType.Outpost;

    /// <summary>
    /// Enemy emplacements we know the position of. Neutral ones are left out: RelationTo reports
    /// Neutral whenever either side is factionless, which covers plenty of things that never
    /// shoot at anybody.
    /// </summary>
    private List<SpaceObj> HostileStations() =>
        !AvoidHostileStations ? [] :
        _world.Snapshot()
            .Where(o => IsEmplacement(o) && o.HasPosition && _world.RelationTo(o.Id) == Relation.Enemy)
            .ToList();

    /// <summary>True if this object sits inside the reach of an enemy emplacement.</summary>
    private bool InStationDanger(SpaceObj o)
    {
        if (!AvoidHostileStations || !o.HasPosition) return false;
        foreach (var s in HostileStations())
            if (Vector3.Distance(s.Position, o.Position) <= HostileStationKeepOut) return true;
        return false;
    }

    /// <summary>The enemy emplacement we are currently too close to, nearest first.</summary>
    private SpaceObj? StationTooClose()
    {
        if (!_world.MyPositionKnown) return null;
        return HostileStations()
            .Where(s => (_world.DistanceToMe(s) ?? float.MaxValue) <= HostileStationKeepOut)
            .OrderBy(s => _world.DistanceToMe(s) ?? float.MaxValue)
            .FirstOrDefault();
    }

    /// <summary>
    /// Back out of a station's envelope. Guns off and nose straight out: there is nothing to win
    /// here, and the thing shooting at us cannot follow.
    /// </summary>
    private async Task LeaveStationDangerAsync(SpaceObj station)
    {
        await StopAllTogglesAsync();
        lock (_gate) { _target = 0; _lockedTarget = 0; }

        float dist = _world.DistanceToMe(station) ?? 0f;
        var now = DateTime.UtcNow;

        await RunInDirection(_world.MyPosition - station.PredictedPosition(now), now);

        Status = $"Backing out of {station} — {dist:F0}u, want {HostileStationKeepOut:F0}u";
    }

    // ------------------------------------------------------------------ mining

    /// <summary>
    /// What decides where to sit on a rock. A real mining laser if you have one, otherwise your
    /// guns: an autocannon breaks an asteroid open perfectly well, and refusing to fire because
    /// no slot advertised a mining stat left the bot parked in range doing nothing.
    ///
    /// This is the positioning set, not the firing set — see <see cref="MiningFireSet"/>. A
    /// mining laser is typically much shorter-ranged than a cannon, and holding station at the
    /// cannon's reach would put the laser out of range, so the laser alone sets the standoff.
    /// </summary>
    private (List<Weapon> Guns, bool Improvised) MiningWeapons()
    {
        var lasers = Weapons.For(WeaponRole.Mining);
        if (lasers.Count > 0) return (lasers, false);
        return (Weapons.For(WeaponRole.Combat), true);
    }

    /// <summary>
    /// Everything that actually shoots the rock. Owning one mining laser used to silence every
    /// other gun on the ship: the laser set the role, and the cannons — enabled, in range, idle —
    /// were never asked. They break rocks too, so unless you turn <see cref="FireGunsWhileMining"/>
    /// off, they fire alongside it. FireAll still range-checks each one, so a cannon with a dead
    /// zone at knife range simply skips its turn.
    /// </summary>
    private List<Weapon> MiningFireSet(List<Weapon> lasers, bool improvised)
    {
        // Improvised already IS the combat list, so there is nothing to add.
        if (improvised || !FireGunsWhileMining) return lasers;

        var all = new List<Weapon>(lasers);
        var seen = lasers.Select(w => w.AbilityId).ToHashSet();
        // For(Combat) also yields unidentified abilities, which For(Mining) already returned —
        // firing one twice in a tick is a double cast the server counts against you.
        foreach (var g in Weapons.For(WeaponRole.Combat))
            if (seen.Add(g.AbilityId)) all.Add(g);
        return all;
    }

    /// <summary>The confirmed resource scanner, if you've ever scanned a rock by hand.</summary>
    private Weapon? Scanner() => Weapons.For(WeaponRole.Scanner).FirstOrDefault();

    // ------------------------------------------------------------------ staying alive

    /// <summary>
    /// Casts your repair module the moment the hull dips, rather than saving it for a rainy day.
    ///
    /// Strike Damage Control restores a flat number of hull points on a 30-second reload, so the
    /// value of holding it back is zero and the cost of holding it back is the run — the reload
    /// is what limits it, not the hull. Fired at the ship, which is what makes it a Repair in the
    /// first place: <see cref="RoleOf"/> learns it from watching you cast one on yourself.
    /// </summary>
    private async Task SelfRepairAsync()
    {
        if (!UseRepairAbility) return;
        if (_world.MyHullFraction is not { } hull || hull >= RepairAtHull) return;
        if (_world.MyObjectId == 0) return;

        var now = DateTime.UtcNow;
        foreach (var w in Weapons.For(WeaponRole.Repair))
        {
            double interval = w.Cooldown is { } cd && cd > 0 ? cd * 1000.0 : RepairIntervalMs;
            if ((now - w.LastFired).TotalMilliseconds < interval) continue;
            if (!CanAfford(w)) continue;

            await _act.CastSlotAbility(w.AbilityId, _world.MyObjectId);
            w.LastFired = now;
            RepairsCast++;
            Log?.Invoke($"Hull {hull:P0} — cast repair #{w.AbilityId}.");
            return;   // one per tick; they share the power pool
        }
    }

    /// <summary>
    /// Casts the scanner at the nearest rock we don't know the contents of.
    ///
    /// One rock per tick: the scan is an ordinary ability with an ordinary cooldown, and the
    /// server drops scan targets that are out of the ability's own MaxRange without answering,
    /// so spraying them would just look like nothing happening.
    /// </summary>
    private async Task ScanSweepAsync()
    {
        var now = DateTime.UtcNow;

        var scanner = Scanner();
        if (scanner is null)
        {
            await ProbeForScannerAsync(now);
            return;
        }
        double interval = scanner.Cooldown is { } cd && cd > 0 ? cd * 1000.0 : ScanIntervalMs;
        if ((now - scanner.LastFired).TotalMilliseconds < interval) return;

        // Nothing downstream reads the answer when we'll mine whatever is nearest anyway.
        if (ScanOnlyWhenFiltering && WantedResource == ResourceType.Any) return;

        // Already holding a queue of confirmed rocks? Then a scan buys nothing but a flat battery.
        // Scanning ran unconditionally before this, so a ship sitting on four known water rocks
        // still spent every spare point identifying a fifth instead of mining the first.
        if (ConfirmedRocks(now) >= ScanQueueDepth) return;

        // A scan the server refuses for want of power is indistinguishable from no scan at all —
        // and one that succeeds by draining the pool leaves the lasers unable to fire.
        if (!CanAffordScan(scanner)) return;

        float range = _world.SlotStat(scanner.AbilityId, ObjectStat.MaxRange) ?? FallbackRange;

        // Area scanner: one cast carries every rock in the radius, exactly as the client's own
        // GetObjectsWithinAOE does. In a dense belt this is the difference between 50 power for
        // one rock and 50 power for a dozen.
        if (scanner.Area == true)
        {
            var batch = _world.Snapshot()
                .Where(o => NeedsScan(o, now) && ScanDue(o.Id, now) && o.HasPosition)
                .Where(o => (_world.DistanceToMe(o) ?? float.MaxValue) <= range)
                .OrderBy(o => _world.DistanceToMe(o) ?? float.MaxValue)
                .Take(MaxAreaScanTargets)
                .Select(o => o.Id)
                .ToArray();

            if (batch.Length == 0) return;

            await _act.CastSlotAbility(scanner.AbilityId, batch);
            scanner.LastFired = now;
            ScansSent++;
            lock (_gate)
                foreach (var id in batch) _scanAsked[id] = now;

            Log?.Invoke($"Area scan: {batch.Length} rock(s) in one cast ({range:F0}u).");
            NoteScanSent();
            return;
        }

        var rock = _world.Nearest(o => NeedsScan(o, now) && ScanDue(o.Id, now));
        if (rock is null) return;
        if ((_world.DistanceToMe(rock) ?? float.MaxValue) > range) return;

        await _act.CastSlotAbility(scanner.AbilityId, rock.Id);
        scanner.LastFired = now;
        ScansSent++;
        lock (_gate) _scanAsked[rock.Id] = now;
        NoteScanSent();
    }

    /// <summary>
    /// Counts casts that went out with no answer. A missing consumable is rejected in
    /// AbilityAction.preFun with no reply of any kind, so "out of power cells" is invisible on
    /// the wire — worth naming explicitly rather than letting it look like a broken scanner.
    /// </summary>
    private void NoteScanSent()
    {
        if (++_scansWithoutReply < 3 || _ammoWarned) return;
        _ammoWarned = true;
        Log?.Invoke("Scanner has been cast 3 times with no reply. Most likely out of power cells "
                  + "(the Experimental module burns one per scan), otherwise out of power or out of range.");
    }

    /// <summary>
    /// Finds the scanner without you having to press anything.
    ///
    /// Nothing on the wire names it. It publishes no damage stat, so the slot stream leaves it
    /// unclassified next to every other utility slot, and the catalogue that would say
    /// "AbilityActionType.ResourceScan" is deliberately not parsed. What DOES identify it is the
    /// server's own answer — so point each unclassified ability at a rock once and keep the one
    /// that comes back with a scan. Costs one wasted cast per utility slot, once per session.
    /// </summary>
    private async Task ProbeForScannerAsync(DateTime now)
    {
        if ((now - _lastProbe).TotalSeconds < ProbeIntervalSeconds) return;

        Weapon? candidate;
        lock (_gate)
            candidate = Weapons.ProbeCandidates().FirstOrDefault(w => !_probed.Contains(w.AbilityId));
        if (candidate is null) return;

        var rock = _world.Nearest(o => EntityTypes.IsMinable(o.Id) && !o.Scanned);
        if (rock is null) return;

        float reach = _world.SlotStat(candidate.AbilityId, ObjectStat.MaxRange) ?? FallbackRange;
        if ((_world.DistanceToMe(rock) ?? float.MaxValue) > reach) return;

        // Each ability gets exactly one probe, so it must not be spent on a cast the server was
        // always going to drop. A brown-out is the likeliest reason a scan produces no reply, and
        // burning the single attempt on one means the scanner is never identified for the session.
        if (!CanAfford(candidate)) return;

        lock (_gate) _probed.Add(candidate.AbilityId);
        _lastProbe = now;

        NoteScanProbe(candidate.AbilityId, [rock.Id]);
        await _act.CastSlotAbility(candidate.AbilityId, rock.Id);
        Log?.Invoke($"Testing ability #{candidate.AbilityId} ({candidate.Role}) on #{rock.Id:X8} — looking for your scanner.");
    }

    /// <summary>
    /// True if we don't currently know what's in this rock. A scan we already have counts only
    /// while it's fresh: the server respawns asteroid resources on a timer and may pick a
    /// different one, so an old scan can send us across the sector for water that is now
    /// titanium — or skip a rock that has since refilled.
    /// </summary>
    private bool NeedsScan(SpaceObj o, DateTime now)
    {
        if (!EntityTypes.IsMinable(o.Id)) return false;
        if (IsSkipped(o.Id)) return false;
        if (o.MiningCooldown > now) return false;      // can't be mined yet, so don't spend a scan on it
        if (!o.Scanned) return true;
        return (now - o.ScannedAt).TotalSeconds > ScanFreshnessSeconds;
    }

    private bool IsProbed(ushort abilityId)
    {
        lock (_gate) return _probed.Contains(abilityId);
    }

    /// <summary>True if this rock has never been asked about, or was asked long enough ago
    /// that the reply is not coming.</summary>
    private bool ScanDue(uint id, DateTime now)
    {
        lock (_gate)
            return !_scanAsked.TryGetValue(id, out var asked)
                || (now - asked).TotalSeconds > ScanRetrySeconds;
    }

    private async Task MineTick()
    {
        var (lasers, improvised) = MiningWeapons();
        var now = DateTime.UtcNow;
        await ScanSweepAsync();

        // A rock we've confirmed beats one we'd have to fly to before finding out — and that's
        // true whether or not a resource filter is set, because a scan also tells us how much is
        // in there. Fall back to unscanned rocks when nothing is confirmed yet, so the ship keeps
        // moving into scanner range of new ones instead of parking.
        bool haveConfirmed = _world.Nearest(o => MiningCandidate(o) && KnownContents(o, now)) is not null;

        // Among confirmed rocks, richest-per-distance beats merely nearest — a full rock one
        // screen further out is worth more than an almost-empty one right here.
        var rock = haveConfirmed
            ? ResolveTarget(o => MiningCandidate(o) && KnownContents(o, DateTime.UtcNow), RockValue, honourPin: true)
            : ResolveTarget(MiningCandidate, honourPin: true);

        if (rock is null)
        {
            Meter.Note(MiningActivity.Idle, now);
            await StopAllTogglesAsync();
            var all = _world.Snapshot();
            int rocks = all.Count(o => EntityTypes.IsMinable(o.Id));
            int located = all.Count(o => EntityTypes.IsMinable(o.Id) && o.HasPosition);
            string filter = WantedResource == ResourceType.Any ? "" : $", filtering for {WantedResource}";
            Status = rocks == 0
                ? "No asteroids in the sector"
                : located == 0
                    ? $"{rocks} asteroid(s) known but none located — the server hasn't sent their WhoIs bodies"
                    : $"{located} asteroid(s) located, all skipped (depleted, on cooldown{filter})";
            return;
        }

        float dist = _world.DistanceToMe(rock) ?? float.MaxValue;
        float range = lasers.Count > 0 ? EffectiveRange(lasers) : FallbackRange;
        float preferred = StandoffFor(rock, lasers);

        if (dist > range)
        {
            Meter.Note(MiningActivity.Travelling, now);
            if (AutoApproach)
            {
                await SteerToward(rock, preferred);
                Status = $"Closing on asteroid #{rock.Id:X8} — {dist:F0}u, hold at {preferred:F0}u"
                       + $" (r{rock.Radius:F0}), {_throttle:F0}u/s {_gear}";
            }
            else
            {
                Status = $"Asteroid #{rock.Id:X8} is {dist:F0}u away, mining reach {range:F0}u (auto-approach off)";
            }
            return;
        }

        // A rock doesn't dodge, but accuracy still falls off past optimal range — close in.
        bool closing = AutoApproach && dist > preferred;
        if (closing) await SteerToward(rock, preferred);
        else await StopThrottleIfMoving();

        // Locking does NOT scan: the server's LockTarget handler only records the target id.
        // The scan is its own ability cast, handled by ScanSweepAsync above.
        await EnsureLocked(rock.Id);

        // Ordering a mining ship costs resources, so it goes out once per rock, not per tick.
        if (UseMiningFacility && rock.Scanned && rock.IsMinable && _facilityOrdered.Add(rock.Id))
        {
            await _act.Mine(rock.Id);
            Log?.Invoke($"Ordered a mining ship to #{rock.Id:X8}.");
        }

        if (lasers.Count == 0)
        {
            Status = "In range of an asteroid, but no weapon known at all — fire once manually";
            return;
        }

        // Filtering means "only mine THIS resource", so an unidentified rock must not be shot on
        // spec — breaking open a titanium rock in water mode is exactly what you asked it not to
        // do. Hold fire until the scan lands. Holding also lets power build back up for the scan,
        // which is what was starving it: the lasers drained the pool the scanner needed.
        if (WantedResource != ResourceType.Any && Scanner() is not null && !KnownContents(rock, DateTime.UtcNow))
        {
            Meter.Note(MiningActivity.Holding, now);
            await StopAllTogglesAsync();
            Status = $"Holding fire on #{rock.Id:X8} at {dist:F0}u — waiting for the scan"
                   + $" (power {_world.MyPower:F0}/{_world.MyMaxPower ?? 0f:F0})";
            return;
        }

        var shooting = MiningFireSet(lasers, improvised);
        int fired = await FireAll(shooting, rock, dist, closing);

        // Closing still counts as travelling even though we're in range and shooting: the point
        // of the split is to show how much of the run is spent getting somewhere.
        Meter.Note(closing ? MiningActivity.Travelling
                 : fired > 0 ? MiningActivity.Firing
                 : MiningActivity.Holding, now);
        string what = KnownContents(rock, DateTime.UtcNow)
            ? $"{NameResource(rock.ResourceGuid)} x{rock.ResourceCount}"
            : rock.Scanned ? "scan stale" : "unscanned";
        string gun = improvised ? "gun(s)" : shooting.Count > lasers.Count ? "slot(s)" : "laser(s)";
        Status = $"Mining #{rock.Id:X8} — {dist:F0}u / {range:F0}u, {what}"
               + (fired > 0 ? $", {fired} {gun} firing" : ", holding (cooldown)")
               + (closing ? $", closing to {preferred:F0}u" : "");
    }

    /// <summary>
    /// How worthwhile a scanned rock is: its resource count, discounted by the trip. The
    /// divisor is in the same order as sector distances, so a rock twice as far needs roughly
    /// twice the resources to be worth passing a nearer one for.
    /// </summary>
    private static float RockValue(SpaceObj o, float distance) =>
        o.ResourceCount / (1f + distance / 1000f);

    /// <summary>A scan we still believe. Anything older has had time to respawn as something
    /// else and is treated as "we don't know" rather than as fact.</summary>
    private bool KnownContents(SpaceObj o, DateTime now) =>
        o.Scanned && (now - o.ScannedAt).TotalSeconds <= ScanFreshnessSeconds;

    /// <summary>
    /// Rocks we have a fresh scan for, that hold what you asked for, and that we could go and
    /// mine right now. This is the queue: while it's deep enough there is nothing a scan could
    /// tell us that would change the next thing we do.
    ///
    /// <see cref="MiningCandidate"/> already drops empties, the wrong resource, rocks on cooldown
    /// and rocks we gave up approaching, so anything counted here is genuinely next in line.
    /// </summary>
    private int ConfirmedRocks(DateTime now) =>
        _world.Snapshot().Count(o => MiningCandidate(o) && KnownContents(o, now));

    private bool MiningCandidate(SpaceObj o)
    {
        if (!EntityTypes.IsMinable(o.Id)) return false;
        if (IsSkipped(o.Id)) return false;

        var now = DateTime.UtcNow;
        if (o.MiningCooldown > now) return false;

        // NOT gated on SpaceObj.IsMinable. The flag in Reply.Scan answers "can a mining ship be
        // ordered here", which the server only ever sets for planetoids — every ordinary
        // asteroid reports false. Treating it as "is this worth shooting" rejected every rock
        // the moment it got scanned. It is used for the facility order below, and nowhere else.
        // A rock parked under an enemy platform's guns is not a rock we can work.
        if (InStationDanger(o)) return false;

        bool known = KnownContents(o, now);
        if (known && o.ResourceCount == 0) return false;

        // Only reject on resource once we actually know what's in it. An unscanned rock — or one
        // whose scan has gone stale — is still worth approaching, because getting closer is what
        // brings it inside scanner range.
        if (WantedResource != ResourceType.Any && known && o.ResourceGuid != (uint)WantedResource)
            return false;

        return true;
    }

    // ------------------------------------------------------------------ firing

    /// <summary>
    /// The longest reach among the weapons we'd actually use — the distance at which it is
    /// worth opening fire at all. Real numbers when the server publishes slot stats; the
    /// configured fallback when it doesn't.
    /// </summary>
    private float EffectiveRange(List<Weapon> guns)
    {
        var known = guns.Where(w => w.MaxRange is > 0).Select(w => w.MaxRange!.Value).ToList();
        return known.Count > 0 ? known.Max() : FallbackRange;
    }

    /// <summary>
    /// The distance we actually want to fight from, which is not the same as the distance we
    /// can reach. A cannon's accuracy is quoted at its OptimalRange — the FANG reaches 750u but
    /// is only accurate at 300 — so parking at the edge of max range means missing nearly every
    /// shot. We aim for a fraction of the SHORTEST optimal range among the guns in play, so
    /// every one of them is inside its good band, and never closer than the longest minimum
    /// range, because a weapon with a dead zone can't fire from inside it.
    /// </summary>
    private float PreferredRange(List<Weapon> guns, float factor)
    {
        var optimal = guns.Where(w => w.OptimalRange is > 0).Select(w => w.OptimalRange!.Value).ToList();
        var max = guns.Where(w => w.MaxRange is > 0).Select(w => w.MaxRange!.Value).ToList();

        float band = optimal.Count > 0 ? optimal.Min()
                   : max.Count > 0 ? max.Min()
                   : FallbackRange;

        float want = band * factor;

        var mins = guns.Where(w => w.MinRange is > 0).Select(w => w.MinRange!.Value).ToList();
        if (mins.Count > 0) want = Math.Max(want, mins.Max() * 1.2f);

        return Math.Max(want, MinimumStandoff);
    }

    /// <summary>
    /// Where to actually stop, for THIS target.
    ///
    /// Every distance on the wire is centre-to-centre, and an asteroid is not a point — closing
    /// to 150u of the centre of a rock that is hundreds of units across is not a firing position,
    /// it's a collision, and collisions kill. So the weapon's preferred range is only a floor:
    /// the real one is the object's own radius plus clearance. Clamped to stay inside weapon
    /// reach, because a standoff we can't shoot from is no use either.
    /// </summary>
    private float StandoffFor(SpaceObj target, List<Weapon> guns)
    {
        float reach = EffectiveRange(guns);

        switch (EntityTypes.Of(target.Id))
        {
            // Told, not guessed. Still clamped to weapon reach — a hold position we can't shoot
            // from would just park the ship next to a rock forever.
            case SpaceEntityType.Asteroid when AsteroidStandoff > 0:
                return Math.Min(AsteroidStandoff, reach * 0.95f);

            // Not clamped: a planetoid is worked by ordering a mining ship, not by shooting it,
            // so there is nothing to stay in weapon range of.
            case SpaceEntityType.Planetoid when PlanetoidStandoff > 0:
                return PlanetoidStandoff;
        }

        // Closing inside the optimal band only buys anything against something that manoeuvres.
        // Anything that sits still gets the full band: same accuracy, more clearance.
        float factor = EntityTypes.IsStatic(target.Id) ? 1f : CloseInFactor;
        float want = PreferredRange(guns, factor);

        float clear = target.Radius > 0
            ? target.Radius * RadiusClearance + MinimumStandoff
            : MinimumStandoff;

        float stop = Math.Max(want, clear);

        // If the object is so large that clearance exceeds our reach, hug the edge of range
        // instead: still outside it, still able to fire.
        if (stop > reach * 0.95f) stop = Math.Max(reach * 0.95f, clear);

        return stop;
    }

    /// <summary>
    /// Whether we have the power points this ability costs. The server checks the same thing in
    /// AbilityAction.preFun and simply returns without doing anything or saying why, so a
    /// browned-out ship looks identical to a broken bot — including a scanner that silently
    /// never scans. Checking here keeps the power for whatever we can actually afford.
    /// </summary>
    private bool CanAfford(Weapon w)
    {
        if (w.PowerCost is not { } cost || cost <= 0f) return true;
        return _world.MyPower >= cost;
    }

    /// <summary>
    /// As <see cref="CanAfford"/>, but keeps a reserve back. A scan is worth far less than the
    /// mining it pays for, so it only goes out when the pool can stand it.
    /// </summary>
    private bool CanAffordScan(Weapon scanner)
    {
        if (scanner.PowerCost is not { } cost || cost <= 0f) return true;
        float reserve = (_world.MyMaxPower ?? 0f) * ScanPowerReserve;
        return _world.MyPower >= cost + reserve;
    }

    /// <summary>
    /// Fires everything that can usefully shoot right now.
    ///
    /// <paramref name="stillClosing"/> is what stops the ship burning its pool on the way in. A
    /// Tornado-P reaches 600u but is quoted at 250u, so opening fire the moment the target
    /// crosses 600 spends most of a power bar on shots taken at the worst end of the accuracy
    /// curve — while still flying in. When we are on our way to a firing position, each weapon
    /// waits for its own optimal band. When we are not — auto-approach off, or already parked —
    /// it fires at whatever range it has, because a weapon holding out for a range we will never
    /// reach is just a weapon that never fires.
    /// </summary>
    private async Task<int> FireAll(List<Weapon> guns, SpaceObj target, float distance,
                                    bool stillClosing = false)
    {
        var now = DateTime.UtcNow;
        int firing = 0;

        foreach (var w in guns)
        {
            if (w.MaxRange is { } max && distance > max) continue;
            if (w.MinRange is { } min && distance < min) continue;
            if (HoldFireUntilOptimal && stillClosing
                && w.OptimalRange is { } opt && opt > 0 && distance > opt) continue;
            if (!CanAfford(w)) continue;

            if (w.Kind == WeaponKind.Toggle)
            {
                if (!w.ToggledOn)
                {
                    await _act.ToggleAbilityOn(w.AbilityId, target.Id);
                    w.ToggledOn = true;
                    w.ToggleTarget = target.Id;
                    ShotsFired++;
                }
                else if (w.ToggleTarget != target.Id)
                {
                    await _act.UpdateAbilityTargets(w.AbilityId, target.Id);
                    w.ToggleTarget = target.Id;
                }
                firing++;
                continue;
            }

            double interval = w.Cooldown is { } cd && cd > 0
                ? cd * 1000.0
                : FallbackFireIntervalMs;

            if ((now - w.LastFired).TotalMilliseconds < interval) continue;

            await _act.CastSlotAbility(w.AbilityId, target.Id);
            w.LastFired = now;
            _lastAnyShot = now;
            ShotsFired++;
            firing++;
        }

        return firing;
    }

    /// <summary>
    /// The moment power was last being spent, for the regen meter.
    ///
    /// A toggle weapon is the case that matters: it is switched on once and then draws
    /// continuously, so the toggle-on timestamp is not "when it last cost us anything" — it is
    /// still costing us now. While any toggle is live, every sample is contaminated.
    /// </summary>
    private DateTime LastPowerSpend(DateTime now) =>
        Weapons.All().Any(w => w.ToggledOn) ? now : _lastAnyShot;

    private async Task StopAllTogglesAsync()
    {
        foreach (var w in Weapons.All())
        {
            if (!w.ToggledOn) continue;
            try { await _act.ToggleAbilityOff(w.AbilityId); } catch { /* session may be gone */ }
            w.ToggledOn = false;
            w.ToggleTarget = 0;
        }
    }

    // ------------------------------------------------------------------ targeting

    private SpaceObj? ResolveTarget(Func<SpaceObj, bool> candidate, Func<SpaceObj, float, float>? score = null,
                                    bool honourPin = false)
    {
        uint current;
        lock (_gate) current = _target;

        // A contact you pinned by hand is checked before the hunting rules, and outlives them.
        // Not honoured on the self-defence path — being shot at is not the moment to keep
        // pointing at the rock you asked for.
        if (honourPin)
        {
            uint pin;
            lock (_gate) pin = _pinned;
            if (pin != 0)
            {
                var held = _world.Get(pin);
                bool shaped = Mode == FarmMode.Mining ? EntityTypes.IsMinable(pin) : !EntityTypes.IsMinable(pin);
                if (held is not null && held.HasPosition && shaped)
                {
                    lock (_gate) _target = pin;
                    return held;
                }
                if (held is null)
                {
                    lock (_gate) { _pinned = 0; if (_target == pin) { _target = 0; _lockedTarget = 0; } }
                    Log?.Invoke("Pinned target is gone — picking targets automatically again.");
                }
            }
        }

        if (current != 0)
        {
            var held = _world.Get(current);
            if (held is not null && candidate(held) && held.HasPosition) return held;
            lock (_gate) { if (_target == current) { _target = 0; _lockedTarget = 0; } }
        }

        // No throttle here on purpose: the search only runs when there is no valid target,
        // and throttling it made the status line alternate between "engaging" and
        // "no hostiles" every other tick.
        _lastRetarget = DateTime.UtcNow;

        var next = score is null ? _world.Nearest(candidate) : _world.Best(candidate, score);
        if (next is null) return null;

        lock (_gate) { _target = next.Id; }
        Log?.Invoke($"Engaging {next} at {_world.DistanceToMe(next):F0}u");
        return next;
    }

    private async Task EnsureLocked(uint id)
    {
        if (_lockedTarget == id) return;
        await _act.LockTarget(id);
        _lockedTarget = id;
    }

    private async Task EnsureSubscribed(uint id)
    {
        if (_subscribedTarget == id) return;
        if (_subscribedTarget != 0)
        {
            try { await _act.UnSubscribeInfo(_subscribedTarget); } catch { }
        }
        await _act.SubscribeInfo(id);
        _subscribedTarget = id;
    }

    private void Skip(uint id, TimeSpan how) { lock (_gate) _skip[id] = DateTime.UtcNow + how; }

    private bool IsSkipped(uint id)
    {
        lock (_gate)
        {
            if (!_skip.TryGetValue(id, out var until)) return false;
            if (until > DateTime.UtcNow) return true;
            _skip.Remove(id);
            return false;
        }
    }

    // ------------------------------------------------------------------ movement

    /// <summary>
    /// My ship's real top speed.
    ///
    /// SetSpeed carries an absolute number, not an intent. <see cref="SpeedMode"/> rides along
    /// in the first byte but no server reads it: the client resolves SpeedMode.Full into
    /// Game.Me.Stats.MaxSpeed on its own side (ShipControlsBase.ChangeCurrentSpeed) and puts
    /// that number in the float. Sending "Full, 1" therefore asked for one unit per second,
    /// which is what made every approach crawl.
    ///
    /// Three sources, in order of how much they can be trusted: a number you typed in, the
    /// ship-wide Speed stat when the server publishes it, and the fastest throttle we've watched
    /// you send.
    ///
    /// Watching your throttle is a LOWER BOUND, not a measurement, and treating it as one is what
    /// made the ship crawl: the client sends an absolute speed on every step of the ramp, so one
    /// tap of the throttle key published 1u/s, and `max(stat, observed)` then took that 1 as the
    /// answer because it was the only non-zero source. Seeing you fly at 12u/s proves the ship
    /// can do at least 12. It proves nothing about the ceiling. So it only ever raises the
    /// fallback, never replaces it.
    /// </summary>
    private float TopSpeed
    {
        get
        {
            if (TopSpeedOverride > 0f) return TopSpeedOverride;

            float stat = _world.ShipStat(ObjectStat.Speed) ?? 0f;
            if (stat > 0f) return stat;

            return Math.Max(_observedTopSpeed, FallbackSpeed);
        }
    }

    /// <summary>
    /// Speed the boost gear gives us. The gear change alone is what applies it — we never put
    /// this number in a SetSpeed, because the stored throttle only takes effect in Regular gear.
    ///
    /// It is still needed for two things that are not "how fast to ask for": deciding whether
    /// boosting is worth it at all, and sizing the braking zone and the obstacle lookahead, both
    /// of which have to be measured against the speed we will ACTUALLY be doing.
    ///
    /// A server that publishes no Speed stat publishes no BoostSpeed either, and the old gate
    /// (<c>BoostSpeed &gt; 0</c>) then silently meant the boost gear was never engaged once, no
    /// matter what the Boost toggle said. Hence the override.
    /// </summary>
    private float BoostSpeed
    {
        get
        {
            if (BoostSpeedOverride > 0f) return BoostSpeedOverride;
            return _world.ShipStat(ObjectStat.BoostSpeed) ?? 0f;
        }
    }

    /// <summary>
    /// How fast we will be travelling in this gear — which is what braking room and obstacle
    /// lookahead must be measured against, NOT the throttle number we send.
    ///
    /// Sizing both from <see cref="TopSpeed"/> while the ship was in boost was a real hole in the
    /// collision avoidance: at 84u/s with the zone computed for 52u/s, every distance it reserved
    /// to turn or stop in was about a third short.
    /// </summary>
    private float SpeedInGear(Gear gear) =>
        gear == Gear.Boost && BoostSpeed > 0f ? BoostSpeed : TopSpeed;

    /// <summary>
    /// Points the ship at the target and opens the throttle. Same messages the client sends
    /// when you fly manually: a heading (Euler3.Direction of the offset), an absolute speed,
    /// and a gear.
    ///
    /// Order matters. SetSpeed stores the throttle and only applies it while the regular gear
    /// is engaged; SetGear(Regular) re-applies whatever throttle was stored last. Sending the
    /// speed first is therefore correct in both directions: entering boost keeps the stored
    /// throttle for when we leave it, and leaving boost lands on the number we just set.
    /// </summary>
    private async Task SteerToward(SpaceObj target, float stopRange, bool watchdog = true)
    {
        var now = DateTime.UtcNow;
        float distance = _world.DistanceToMe(target) ?? float.MaxValue;
        if (watchdog && WatchApproach(target.Id, distance, now)) return;

        var desired = target.PredictedPosition(now) - _world.MyPosition;

        // Gear is decided first because everything below is measured in seconds of travel, and
        // how far a second is depends on which gear we're in. It needs nothing but the distance,
        // so there is no circularity — and if an obstacle forces Regular later, the zones stay
        // sized for boost, which errs towards more room rather than less.
        var gear = UseBoost && BoostSpeed > 0f && distance > stopRange + BoostMargin
            ? Gear.Boost
            : Gear.Regular;
        float flying = SpeedInGear(gear);

        // The zone is how far this ship travels in BrakingSeconds, not a flat number, and it is
        // no longer widened by the standoff. `Max(700, stopRange)` meant a 179u hold on a rock
        // started braking at 879u and crawled the last stretch at MinApproachSpeed — around a
        // minute of creeping across ground the ship could cover in seconds.
        float brakeZone = Math.Clamp(flying * BrakingSeconds, MinBrakeDistance, BrakingDistance);

        // Look no further than the target itself: something past it is not in the way.
        float lookahead = Math.Min(distance, Math.Max(flying * CollisionLookaheadSeconds, brakeZone * 2f));
        var heading = DeflectAroundObstacles(desired, lookahead, target.Id, now, out bool deflected);

        // Heading is rate-limited; the throttle is NOT. Braking has to be able to react on every
        // tick, or the ship spends up to 400ms at full speed while already inside the brake zone.
        // A dodge is on the same clock as the brake, for the same reason — 400ms of holding the
        // old heading is most of the room we have left when something is already close ahead.
        double steerEvery = deflected ? 150 : 400;
        if ((now - _lastSteer).TotalMilliseconds >= steerEvery && heading.LengthSquared() >= 1f)
        {
            _lastSteer = now;
            await _act.MoveToDirection(WorldState.EulerTowards(heading));
        }

        // What we're about to hit is decided by where the ship is actually going, not by where
        // we just asked it to point — a turn takes time, and during it the momentum still runs
        // along the old heading. Measuring the brake against the ordered heading would let the
        // throttle come back up the instant the dodge was ordered, which is the moment it is
        // least earned.
        var blocker = BlockerAhead(Momentum(heading), lookahead, target.Id, now, out float gap);

        // Bleed speed off across the last stretch so we arrive slow. Arriving fast and then
        // cutting the throttle just means coasting through the target — which, on a rock, is
        // a collision rather than an overshoot.
        float throttle = TopSpeed;
        if (distance < stopRange + brakeZone)
        {
            float t = Math.Clamp((distance - stopRange) / brakeZone, 0f, 1f);
            // Square-root taper: full speed for most of the zone, hard braking only at the end,
            // where a linear ramp spends its whole length barely moving.
            throttle = Math.Max(TopSpeed * MathF.Sqrt(t), MinApproachSpeed);
        }

        // Brake for what is in the way as well as for what we're aiming at. Turning takes room,
        // and the whole failure this guards against is arriving at an obstacle with the throttle
        // still set for a target thousands of units behind it. Boost is off outright: there is no
        // approach worth being unable to turn out of.
        if (blocker is not null)
        {
            float t = Math.Clamp(gap / brakeZone, 0f, 1f);
            throttle = Math.Min(throttle, Math.Max(TopSpeed * MathF.Sqrt(t), MinApproachSpeed));
            gear = Gear.Regular;
            NoteDodge(blocker, gap, now);
        }

        bool changed = !_throttleOpen
                     || gear != _gear
                     || Math.Abs(throttle - _throttle) > 0.5f;

        // Resend periodically anyway: a jump, a death or a stat change can reset the server's
        // idea of our throttle without telling us.
        if (!changed && (now - _lastThrottle).TotalSeconds <= 5) return;

        await _act.SetSpeed(SpeedMode.Abs, throttle);
        await _act.SetGear(gear);

        if (changed && _throttleOpen && gear != _gear)
            Log?.Invoke($"Gear {gear} at {distance:F0}u.");

        _throttleOpen = true;
        _throttle = throttle;
        _gear = gear;
        _lastThrottle = now;
    }

    // ------------------------------------------------------------------ collision avoidance

    /// <summary>
    /// The nearest solid object our path runs into, and how much room is left before we reach it.
    ///
    /// The test is the closest approach of the ray to each obstacle's centre. Inside the
    /// obstacle's radius plus clearance, and ahead of us rather than behind, means we are on a
    /// collision course. <paramref name="gap"/> is the distance still to run before contact,
    /// which is what the brake needs — not the centre-to-centre distance.
    ///
    /// <paramref name="ignoreId"/> is the target we are deliberately flying at: the standoff
    /// logic already decides how close is safe for that one.
    /// </summary>
    private SpaceObj? BlockerAhead(Vector3 heading, float lookahead, uint ignoreId, DateTime now,
                                   out float gap)
    {
        gap = float.MaxValue;

        if (!AvoidCollisions || !_world.MyPositionKnown || heading.LengthSquared() < 1e-4f) return null;

        var me = _world.MyPosition;
        var dir = Vector3.Normalize(heading);

        SpaceObj? nearest = null;
        float nearestAlong = 0f;

        foreach (var o in _world.Snapshot())
        {
            if (o.IsMe || o.Id == ignoreId || o.Id == _world.MyObjectId) continue;
            if (!o.HasPosition || !EntityTypes.IsSolid(o.Id)) continue;

            var toObs = o.PredictedPosition(now) - me;
            float clear = o.Radius + CollisionMargin;

            // How far along our heading it sits. Negative means it is behind us, and flying away
            // from something is never the problem.
            float along = Vector3.Dot(toObs, dir);
            if (along <= 0f) continue;
            if (along - clear > lookahead) continue;

            // Perpendicular distance from the path to its centre. Anything wider than the
            // clearance we simply fly past.
            float lateral = MathF.Sqrt(Math.Max(toObs.LengthSquared() - along * along, 0f));
            if (lateral >= clear) continue;

            // Nearest first — clearing that also buys time to see the ones behind it.
            if (nearest is null || along < nearestAlong)
            {
                nearest = o;
                nearestAlong = along;
                gap = Math.Max(along - clear, 0f);
            }
        }

        return nearest;
    }

    /// <summary>
    /// Turns a "point straight at it" heading into one that misses everything solid on the way.
    ///
    /// When something blocks the direct line, the heading is swung to a point just outside that
    /// obstacle's near edge — the shortest deflection that clears it. Recomputed every tick from
    /// the direct line, so the path curves around the obstacle and snaps back to the target the
    /// moment it is no longer in front: no waypoints to store and get stale.
    /// </summary>
    private Vector3 DeflectAroundObstacles(Vector3 desired, float lookahead, uint ignoreId,
                                           DateTime now, out bool deflected)
    {
        deflected = false;
        if (desired.LengthSquared() < 1f) return desired;

        var blocker = BlockerAhead(desired, lookahead, ignoreId, now, out _);
        if (blocker is null) return desired;

        var me = _world.MyPosition;
        var dir = Vector3.Normalize(desired);
        var obs = blocker.PredictedPosition(now);
        var toObs = obs - me;

        float along = Vector3.Dot(toObs, dir);
        float clear = blocker.Radius + CollisionMargin;

        // Push the aim to whichever side our path already favours: that is the smaller course
        // change, and it keeps the deflection stable instead of flip-flopping between the two
        // ways round on consecutive ticks.
        var offset = dir * along - toObs;
        float lateral = offset.Length();
        var side = lateral > 1f ? offset / lateral : SidestepAxis(dir);

        var aim = obs + side * (clear * 1.25f) - me;
        if (aim.LengthSquared() < 1f) return desired;

        deflected = true;
        return aim;
    }

    /// <summary>
    /// Which way the ship is actually travelling, for deciding what it is about to hit. Falls
    /// back to the ordered heading when we're stationary or the server hasn't sent a velocity —
    /// at rest the two are the same thing anyway.
    /// </summary>
    private Vector3 Momentum(Vector3 ordered) =>
        _world.MyVelocity.LengthSquared() > 1f ? _world.MyVelocity : ordered;

    /// <summary>
    /// A way to dodge when the obstacle is dead ahead and there is no "side" to prefer. Any
    /// direction perpendicular to our heading will do; the cross with world up keeps it a flat
    /// turn where possible, which is what the ship is fastest at.
    /// </summary>
    private static Vector3 SidestepAxis(Vector3 dir)
    {
        var axis = Vector3.Cross(dir, Vector3.UnitY);
        if (axis.LengthSquared() < 0.01f) axis = Vector3.Cross(dir, Vector3.UnitX);
        return Vector3.Normalize(axis);
    }

    /// <summary>One line per obstacle, not one per tick — a dodge lasts several seconds and the
    /// log is meant to be readable.</summary>
    private void NoteDodge(SpaceObj blocker, float gap, DateTime now)
    {
        if (_dodgeId == blocker.Id && (now - _dodgeSince).TotalSeconds < 10) return;
        _dodgeId = blocker.Id;
        _dodgeSince = now;
        NearMisses++;
        Log?.Invoke($"Braking and steering around {blocker} — {gap:F0}u of room left ahead.");
    }

    /// <summary>
    /// Full throttle along a heading of our own choosing — running from something rather than
    /// flying to something. Goes through the same obstacle check as an approach: the straight
    /// line away from a threat is no less likely to have a rock in it, and this is the path that
    /// runs with the boost lit.
    /// </summary>
    private async Task RunInDirection(Vector3 want, DateTime now)
    {
        // Running is the fastest the ship ever goes, so the room it reserves to turn and stop has
        // to be sized for the boosted speed, not the throttle number.
        var gear = UseBoost && BoostSpeed > 0f ? Gear.Boost : Gear.Regular;
        float flying = SpeedInGear(gear);

        float brakeZone = Math.Clamp(flying * BrakingSeconds, MinBrakeDistance, BrakingDistance);
        float lookahead = Math.Max(flying * CollisionLookaheadSeconds, brakeZone * 2f);

        var heading = DeflectAroundObstacles(want, lookahead, 0, now, out bool deflected);

        if ((now - _lastSteer).TotalMilliseconds >= (deflected ? 150 : 400) && heading.LengthSquared() >= 1f)
        {
            _lastSteer = now;
            await _act.MoveToDirection(WorldState.EulerTowards(heading));
        }

        float throttle = TopSpeed;

        var blocker = BlockerAhead(Momentum(heading), lookahead, 0, now, out float gap);
        if (blocker is not null)
        {
            float t = Math.Clamp(gap / brakeZone, 0f, 1f);
            throttle = Math.Max(TopSpeed * MathF.Sqrt(t), MinApproachSpeed);
            gear = Gear.Regular;
            NoteDodge(blocker, gap, now);
        }

        bool changed = !_throttleOpen || gear != _gear || Math.Abs(throttle - _throttle) > 0.5f;
        if (!changed && (now - _lastThrottle).TotalSeconds <= 5) return;

        await _act.SetSpeed(SpeedMode.Abs, throttle);
        await _act.SetGear(gear);
        _throttleOpen = true;
        _throttle = throttle;
        _gear = gear;
        _lastThrottle = now;
    }

    /// <summary>
    /// Run. Guns off, nose pointed directly away from whatever is shooting, full throttle and
    /// boost if we have it.
    ///
    /// The old behaviour was <c>DisengageAsync</c> — stop the engines, drop the target, sit
    /// still. Against an NPC that has already locked you, holding station at zero speed next to
    /// a rock is the worst thing you can do, and it is how the Raptor died.
    /// </summary>
    private async Task FleeTick(float hull)
    {
        await StopAllTogglesAsync();
        lock (_gate) { _target = 0; _lockedTarget = 0; }

        var threat = NearestDanger();
        var now = DateTime.UtcNow;

        // Running into open space only postpones it — an NPC that out-runs you catches you with
        // less hull than you started with. A friendly outpost is somewhere to *arrive*: it shoots
        // back, and it is a place to dock.
        //
        // The refuge is worth heading for even when nothing hostile can be named. Not being able
        // to identify what took the hull off is not evidence of safety, and the old "hold to
        // recover" branch parked the ship in the open right where it was being shot.
        var refuge = threat is not null ? SafeOutpost(threat) ?? NearestRefuge() : NearestRefuge();
        if (refuge is not null)
        {
            float gap = _world.DistanceToMe(refuge) ?? float.MaxValue;
            float ask = DockRange(refuge);
            string chased = threat is not null
                ? $", {threat} {_world.DistanceToMe(threat) ?? 0f:F0}u behind"
                : "";

            if (gap > ask)
            {
                await SteerToward(refuge, ask);
                Status = $"HULL {hull:P0} — RUNNING to {refuge} ({gap:F0}u){chased}";
                return;
            }

            // Arrived. Hovering at the door is still being in the open, so put the ship inside.
            // Rate-limited like every other dock request: an over-range attempt is logged as
            // cheating with your player id on it.
            await StopThrottleIfMoving();
            if ((now - _dockAsked).TotalSeconds >= 4)
            {
                _dockAsked = now;
                await _act.Dock(refuge.Id);
                Log?.Invoke($"Retreat: dock requested at #{refuge.Id:X8} from {gap:F0}u.");
            }
            Status = $"HULL {hull:P0} — docking at {refuge} ({gap:F0}u){chased}";
            return;
        }

        if (threat is null)
        {
            await StopThrottleIfMoving();
            Status = $"HULL {hull:P0} — nowhere to dock and nothing hostile nearby, holding";
            return;
        }

        float away = _world.DistanceToMe(threat) ?? 0f;

        await RunInDirection(_world.MyPosition - threat.PredictedPosition(now), now);

        lock (_gate) { _target = 0; _lockedTarget = 0; }
        Status = $"HULL {hull:P0} — RUNNING from {threat} ({away:F0}u), {_throttle:F0}u/s {_gear}";
    }

    /// <summary>
    /// The friendly station worth running to. "Friendly" is our own faction and group, so an
    /// enemy outpost — which is also dockable-shaped — can never be picked as a refuge.
    ///
    /// Rejects any station that would take us past the threat: flying through the thing chasing
    /// us to reach safety is worse than flying away from it. The test is the angle between "to
    /// the station" and "to the threat", so a refuge roughly behind us always wins.
    /// </summary>
    /// <summary>
    /// How close to get before asking to dock. A range we've watched work beats a guess;
    /// otherwise get properly close, because the server treats an over-range dock as cheating
    /// rather than as a miss.
    /// </summary>
    private float DockRange(SpaceObj station) =>
        _learnedDockRange > 0
            ? _learnedDockRange * 0.9f
            : Math.Max(DockApproach, station.Radius * RadiusClearance + MinimumStandoff);

    /// <summary>Nearest friendly place to dock, with no opinion about which way the threat is.</summary>
    private SpaceObj? NearestRefuge() =>
        FleeToOutpost
            ? _world.Nearest(o => EntityTypes.IsDockable(o.Id) && !o.Cloaked
                               && _world.RelationTo(o.Id) is Relation.Friend or Relation.Self)
            : null;

    private SpaceObj? SafeOutpost(SpaceObj threat)
    {
        if (!FleeToOutpost || !_world.MyPositionKnown) return null;

        var me = _world.MyPosition;
        var toThreat = threat.PredictedPosition(DateTime.UtcNow) - me;
        if (toThreat.LengthSquared() < 1f) return null;
        toThreat = Vector3.Normalize(toThreat);

        return _world.Snapshot()
            .Where(o => EntityTypes.IsDockable(o.Id) && o.HasPosition && !o.Cloaked)
            .Where(o => _world.RelationTo(o.Id) is Relation.Friend or Relation.Self)
            .Where(o =>
            {
                var toStation = o.Position - me;
                if (toStation.LengthSquared() < 1f) return false;
                // Positive dot means the station lies the same way as the threat. Allow a little,
                // but not "straight through it".
                return Vector3.Dot(Vector3.Normalize(toStation), toThreat) < 0.35f;
            })
            .OrderBy(o => _world.DistanceToMe(o) ?? float.MaxValue)
            .FirstOrDefault();
    }

    /// <summary>
    /// The measured half of the diagnostics: what the ship actually earns, as opposed to what
    /// the item cards say it should. Every line here is silent until it has real data behind it,
    /// because a made-up number is worse than a missing one.
    /// </summary>
    private void AddMeterLines(List<string> lines, List<Weapon> mineGuns, DateTime now)
    {
        lines.Add("");

        string regen = Meter.Regen is { } r
            ? $"{r:F2}/sec measured over {Meter.RegenSampleSeconds:F0}s of quiet"
            : $"measuring… ({Meter.RegenSampleSeconds:F0}s so far — needs the guns off)";
        lines.Add($"power regen    {regen}");

        var cap = Meter.Capacity(mineGuns, _world);
        if (cap is not null)
        {
            lines.Add($"mining draw    {cap.Guns} gun(s), {cap.DrawPerSecond:F1} power/sec, "
                    + $"{cap.RawDamagePerSecond:F1} dmg/sec if power were free");
            if (cap.SustainedDamagePerSecond is { } sus)
            {
                string verdict = cap.PowerLimited
                    ? $"POWER-LIMITED — recharge feeds {cap.SustainableGuns:F1} of {cap.Guns} gun(s)"
                    : "not power-limited — the guns are the ceiling";
                lines.Add($"mining rate    {sus:F1} dmg/sec sustained "
                        + $"({cap.DamagePerPower:F2} dmg per power) — {verdict}");
            }
        }
        else if (mineGuns.Count > 0)
        {
            lines.Add("mining draw    slot stats haven't published cost/cooldown yet");
        }

        double tracked = Meter.TotalTrackedSeconds;
        if (tracked > 5)
            lines.Add($"time split     {Meter.FractionIn(MiningActivity.Firing):P0} firing, "
                    + $"{Meter.FractionIn(MiningActivity.Travelling):P0} travelling, "
                    + $"{Meter.FractionIn(MiningActivity.Holding):P0} holding, "
                    + $"{Meter.FractionIn(MiningActivity.Idle):P0} idle "
                    + $"(over {tracked / 60.0:F1} min)");

        long total = Meter.TotalGained;
        if (total > 0)
        {
            var span = Meter.Elapsed(now);
            string ore = Meter.MinedPerHour(now) is { } mph ? $"{mph:F0} ore/hour" : "…";
            lines.Add($"mined          {Meter.MinedGained} units in {span.TotalMinutes:F1} min "
                    + $"= {ore}   <-- the hull comparison number");
            if (total != Meter.MinedGained)
                lines.Add($"banked         {total} units total (includes loot and non-ore)");
            foreach (var (guid, count) in Meter.AllGained().Take(5))
                lines.Add($"                 {NameResource(guid)} {count}");
        }
        else
        {
            lines.Add("mined          nothing yet — waiting on the first HoldItems from the server");
        }
    }

    /// <summary>Drops what we believe about the server's throttle without touching the wire —
    /// after a jump or a respawn the ship is new and everything we cached is a guess.</summary>
    private void ForgetThrottle()
    {
        _throttleOpen = false;
        _throttle = 0f;
        _gear = Gear.Regular;
        _approachId = 0;
    }

    private async Task StopThrottleIfMoving()
    {
        _approachId = 0;
        if (!_throttleOpen) return;

        // Zero the stored throttle before leaving boost, or SetGear(Regular) would restore the
        // approach speed we just asked to shed.
        await _act.SetSpeed(SpeedMode.Stop, 0f);
        await _act.SetGear(Gear.Regular);

        _throttleOpen = false;
        _throttle = 0f;
        _gear = Gear.Regular;
    }

    /// <summary>
    /// Returns true if the current target should be abandoned. Something we've been flying at
    /// for half a minute without closing any distance is behind geometry, anchored, or being
    /// towed away — chasing it forever is how a farm loop quietly stops farming.
    /// </summary>
    private bool WatchApproach(uint id, float distance, DateTime now)
    {
        if (_approachId != id)
        {
            _approachId = id;
            _approachSince = now;
            _approachBestDistance = distance;
            return false;
        }

        if (distance < _approachBestDistance - 1f)
        {
            _approachBestDistance = distance;
            _approachSince = now;
            return false;
        }

        if ((now - _approachSince).TotalSeconds < 30) return false;

        Skip(id, TimeSpan.FromMinutes(2));
        lock (_gate) { if (_target == id) { _target = 0; _lockedTarget = 0; } }
        _approachId = 0;
        Log?.Invoke($"#{id:X8} stayed {distance:F0}u away for 30s — skipping it for 2 minutes.");
        return true;
    }

    private async Task DisengageAsync(string why)
    {
        await StopAllTogglesAsync();
        try { await StopThrottleIfMoving(); } catch { }
        if (_subscribedTarget != 0)
        {
            try { await _act.UnSubscribeInfo(_subscribedTarget); } catch { }
            _subscribedTarget = 0;
        }
        lock (_gate) { _target = 0; _lockedTarget = 0; }
        Log?.Invoke($"Disengaged ({why}).");
    }

    // ------------------------------------------------------------------ loot

    /// <summary>Wrecks and cargo don't come to you — ask for anything already within reach.</summary>
    private async Task SweepLootAsync()
    {
        foreach (var o in _world.Snapshot())
        {
            if (!EntityTypes.IsLootable(o.Id) || !o.HasPosition) continue;
            lock (_gate) { if (!_lootAsked.Add(o.Id)) continue; }

            float reach = o.Radius > 0 ? Math.Max(o.Radius, 100f) : LootRange;
            float dist = _world.DistanceToMe(o) ?? float.MaxValue;
            if (dist > reach)
            {
                lock (_gate) _lootAsked.Remove(o.Id);   // retry when we're closer
                continue;
            }

            await TryLootAsync(o.Id, o.CargoAction);
        }
    }

    private async Task TryLootAsync(uint id, CargoInteraction action = CargoInteraction.None)
    {
        try
        {
            if (action is CargoInteraction.Pickup or CargoInteraction.Dropoff)
                await _act.SendCargoInteraction(id, action);
            else
                await _act.RequestLoot(id);
        }
        catch { /* session gone */ }
    }

    // ------------------------------------------------------------------ diagnostics

    /// <summary>
    /// Answers, in plain numbers, the two questions the status line can only hint at:
    /// what does the bot actually know, and how far away is everything.
    /// </summary>
    public string Diagnostics()
    {
        var objs = _world.Snapshot();
        var lines = new List<string>();

        lines.Add($"my player id   {(_world.MyPlayerId == 0 ? "unknown" : _world.MyPlayerId.ToString())}");
        lines.Add($"my ship        {(_world.MyObjectId == 0 ? "unknown" : $"#{_world.MyObjectId:X8} {_world.MyFaction}/{_world.MyGroup}")}");
        lines.Add(_world.MyPositionKnown
            ? $"my position    {_world.MyPosition.X:F0}, {_world.MyPosition.Y:F0}, {_world.MyPosition.Z:F0}"
            : "my position    unknown");
        lines.Add($"hull           {Points(_world.MyHull, _world.MyMaxHull, _world.MyHullFraction)}");
        lines.Add($"power          {Points(_world.MyPower, _world.MyMaxPower, _world.MyPowerFraction)}");

        string speedSource = TopSpeedOverride > 0f ? "set by hand"
                           : _world.ShipStat(ObjectStat.Speed) is > 0 ? "ship stat"
                           : _observedTopSpeed > FallbackSpeed ? "watched your throttle"
                           : "fallback, nothing published";
        string boostSource = BoostSpeedOverride > 0f ? "set by hand"
                           : _world.ShipStat(ObjectStat.BoostSpeed) is > 0 ? "ship stat"
                           : "never published";
        lines.Add($"throttle       {TopSpeed:F0}u/s ({speedSource})");
        lines.Add($"boost          " + (BoostSpeed > 0f
            ? $"{BoostSpeed:F0}u/s ({boostSource})"
              + (UseBoost ? $", engaged past {BoostMargin:F0}u" : ", toggle is OFF")
            : $"unusable — no BoostSpeed ({boostSource}), so the gear is never engaged"));
        lines.Add($"flying         {(_throttleOpen ? $"{_throttle:F0}u/s in {_gear}" : "stopped")}");
        lines.Add("");

        var guns = Weapons.For(WeaponRole.Combat);
        var (mineGuns, improvised) = MiningWeapons();
        lines.Add($"combat reach   {(guns.Count == 0 ? "no weapon known" : $"{EffectiveRange(guns):F0}u, sit at {PreferredRange(guns, CloseInFactor):F0}u + target size")}");
        lines.Add($"hold off       asteroid {AsteroidStandoff:F0}u, planetoid {PlanetoidStandoff:F0}u");

        if (AvoidCollisions)
        {
            var ahead = BlockerAhead(_world.MyVelocity, TopSpeed * CollisionLookaheadSeconds,
                                     CurrentTarget, DateTime.UtcNow, out float room);
            lines.Add($"collisions     avoiding, radius +{CollisionMargin:F0}u, looking "
                    + $"{TopSpeed * CollisionLookaheadSeconds:F0}u ahead — "
                    + (_world.MyVelocity.LengthSquared() < 1f ? "not moving"
                       : ahead is null ? "path clear"
                       : $"{ahead} at {room:F0}u")
                    + $", {NearMisses} avoided");
        }
        else
        {
            lines.Add($"collisions     NOT avoided (off) — {NearMisses} avoided before it was turned off");
        }
        lines.Add($"mining reach   {(mineGuns.Count == 0 ? "no weapon known" : $"{EffectiveRange(mineGuns):F0}u")}"
                + (improvised && mineGuns.Count > 0 ? " (no laser — using your guns)" : ""));
        if (mineGuns.Count > 0)
        {
            var mineFire = MiningFireSet(mineGuns, improvised);
            lines.Add($"mining fires   {string.Join(", ", mineFire.Select(w => $"{w.Label} {w.Role}"))}"
                    + (FireGunsWhileMining ? "" : "  (guns-on-rocks off)"));
        }

        var scanner = Scanner();
        var nowD = DateTime.UtcNow;
        int unknown = objs.Count(o => NeedsScan(o, nowD));
        int known = objs.Count(o => EntityTypes.IsMinable(o.Id) && KnownContents(o, nowD));

        if (scanner is not null)
        {
            string gate = ScanOnlyWhenFiltering && WantedResource == ResourceType.Any
                ? "idle (no resource filter set)"
                : ConfirmedRocks(nowD) >= ScanQueueDepth
                    ? $"idle (queue full — {ConfirmedRocks(nowD)} confirmed, wants {ScanQueueDepth})"
                    : CanAffordScan(scanner) ? "ready" : "waiting for power";
            string kind = scanner.Area switch
            {
                true => "area",
                false => "single-target",
                null => "area unknown — scan once by hand to settle it",
            };
            lines.Add($"scanner        ability #{scanner.AbilityId}, {kind}, reach "
                    + $"{_world.SlotStat(scanner.AbilityId, ObjectStat.MaxRange) ?? FallbackRange:F0}u, "
                    + $"costs {scanner.PowerCost ?? 0f:F0} power — {gate}");
        }
        else
        {
            int left = Weapons.ProbeCandidates().Count(w => !IsProbed(w.AbilityId));
            lines.Add($"scanner        not found — {left} ability(s) left to test"
                    + (left == 0 ? " (none of yours answered a scan)" : ""));
        }
        lines.Add($"rock contents  {known} known, {unknown} unknown ({ScansSent} scans sent)");
        lines.Add($"mining queue   {ConfirmedRocks(nowD)} confirmed and worth mining, "
                + $"stops scanning at {ScanQueueDepth}, scans trusted {ScanFreshnessSeconds}s");

        AddMeterLines(lines, mineGuns, nowD);

        var repairs = Weapons.For(WeaponRole.Repair);
        lines.Add($"repair         {(repairs.Count == 0
            ? "none known — cast Damage Control once by hand to teach it"
            : $"{string.Join(", ", repairs.Select(w => w.Label))} below {RepairAtHull:P0} hull"
              + $" ({RepairsCast} cast)")}"
                + (UseRepairAbility ? "" : "  (off)"));

        var stations = HostileStations();
        string nearestStation = stations
            .OrderBy(s => _world.DistanceToMe(s) ?? float.MaxValue)
            .Select(s => $"{s} at {_world.DistanceToMe(s) ?? 0f:F0}u")
            .FirstOrDefault() ?? "none located";
        lines.Add($"enemy stations {(AvoidHostileStations
            ? $"avoiding within {HostileStationKeepOut:F0}u — {nearestStation}"
            : "not avoided (off)")}");
        lines.Add($"firing         {(HoldFireUntilOptimal
            ? "holds each weapon for its optimal range while closing"
            : "opens up at max range")}");
        lines.Add($"hunting        {(Prey.Count == 0 ? "any NPC" : string.Join(", ", Prey))}"
                + (AttackPlayers ? " + players" : ""));
        lines.Add($"mining for     {(WantedResource == ResourceType.Any ? "any resource" : WantedResource.ToString())}");

        // What the server said is bolted to the ship, against what you told the bot it is.
        var slots = _world.MySlots();
        int declared = Weapons.All().Count(w => w.RoleFromUser || w.Name.Length > 0);
        lines.Add($"loadout        {(slots.Count == 0
            ? "no slot list from this server"
            : $"{slots.Count(s => s.Filled)} of {slots.Count} slots filled")}"
                + $", {declared} declared by you");
        if (_pinned != 0) lines.Add($"pinned         #{_pinned:X8} — held ahead of the hunting rules");
        if (_following)
        {
            var chased = _world.Get(_followTarget);
            lines.Add($"flying to      {chased?.ToString() ?? $"#{_followTarget:X8}"} at "
                    + $"{(chased is not null ? _world.DistanceToMe(chased) ?? 0f : 0f):F0}u, "
                    + $"holding {(chased is not null ? FollowStandoff(chased) : FollowDistance):F0}u"
                    + (_followHold ? _followLosingGround ? " — losing ground, still chasing" : " — keeping station"
                                   : " — stops on arrival"));
        }
        foreach (var w in Weapons.All()) lines.Add("  " + w.Describe());
        if (Weapons.Count == 0) lines.Add("  (nothing learned yet)");
        lines.Add("");

        AddNearest(lines, "nearest hostile", objs.Where(CombatCandidate));
        AddNearest(lines, "nearest rock", objs.Where(MiningCandidate));

        int unlocated = objs.Count(o => !o.HasPosition);
        lines.Add($"objects        {objs.Count} known, {unlocated} without a position");

        // What your own client is filtering out, using its own DradisHelper rule.
        var det = _world.Detection;
        if (det.Known)
        {
            var bands = objs.Where(o => !o.IsMe && o.HasPosition)
                            .GroupBy(o => _world.LayerOf(o, det))
                            .ToDictionary(gr => gr.Key, gr => gr.Count());
            lines.Add($"detection      dradis {det.Dradis:F0}u, map {det.Map:F0}u, visual {det.Visual:F0}u");
            lines.Add($"bands          visual {bands.GetValueOrDefault(ContactLayer.Visual)}"
                    + $", dradis {bands.GetValueOrDefault(ContactLayer.Dradis)}"
                    + $", map {bands.GetValueOrDefault(ContactLayer.Map)}"
                    + $", dark {bands.GetValueOrDefault(ContactLayer.Dark)}");
        }
        else
        {
            lines.Add("detection      radii not published by this server");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private void AddNearest(List<string> lines, string label, IEnumerable<SpaceObj> pool)
    {
        var located = pool.Where(o => o.HasPosition).ToList();
        if (located.Count == 0) { lines.Add($"{label,-14} none located"); return; }

        var now = DateTime.UtcNow;
        var best = located.OrderBy(o => Vector3.Distance(o.PredictedPosition(now), _world.MyPosition)).First();
        string extra = best.Scanned ? $"  {NameResource(best.ResourceGuid)} x{best.ResourceCount}"
                     : best.StatsKnown ? $"  hull {best.Hull:F0}"
                     : "";
        lines.Add($"{label,-14} {best} at {_world.DistanceToMe(best):F0}u{extra}");
    }

    /// <summary>"430 / 495 (87%)", degrading to bare points until the server publishes a maximum.</summary>
    private static string Points(float value, float? max, float? fraction) =>
        max is > 0 && fraction is not null
            ? $"{value:F0} / {max.Value:F0} ({fraction.Value:P0})"
            : $"{value:F0} points (maximum not published)";

    /// <summary>Maps a resource card guid back to its name, for anything the client knows about.</summary>
    private static string NameResource(uint guid) =>
        Enum.IsDefined(typeof(ResourceType), guid) ? ((ResourceType)guid).ToString() : $"resource {guid}";
}
