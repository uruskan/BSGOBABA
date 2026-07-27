using System.Numerics;
using BsgoBot.Cards;
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
    private readonly System.Threading.Timer _cardTimer;
    private int _cardBusy;
    private DateTime _lastCardSave = DateTime.UtcNow;

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

    /// <summary>When the current unbroken detour began, or MinValue if we are not detouring.
    /// Bounds how long steering around something may excuse making no progress.</summary>
    private DateTime _detourSince = DateTime.MinValue;

    // Mining watchdog. The approach watchdog only runs while we are closing on something, so
    // once the ship reached its standoff nothing re-checked the target ever again — which is how
    // it spent an evening shooting an asteroid that was not there.
    private uint _mineWatchId;
    private DateTime _mineProgressAt;
    private float _mineHull;
    private uint _mineOreLeft;
    private long _mineOreBanked;

    // Position-fix bookkeeping. _movedAt is the last tick the ship was under way; _fixWaitSince
    // is when we stopped to ask the server where we really are, and _fixWaitGaveUp latches once
    // that question has gone unanswered long enough to stop being worth asking.
    private DateTime _movedAt = DateTime.MinValue;
    private DateTime _fixWaitSince = DateTime.MinValue;
    private bool _fixWaitGaveUp;
    private bool _fixWaitWarned;

    /// <summary>Times the ship stopped on arrival to confirm where it was, for diagnostics.</summary>
    public int PositionResyncs { get; private set; }

    // The obstacle we are currently steering around, so the log gets one line per dodge.
    private uint _dodgeId;
    private DateTime _dodgeSince = DateTime.MinValue;

    /// <summary>
    /// The obstacle we are currently backing OUT of, as opposed to steering around.
    ///
    /// Being inside a body's clearance sphere is a different state from having one in the way,
    /// and it needs to be remembered rather than recomputed. Without it the ship leaves the
    /// sphere by a single unit, immediately re-aims at a target on the far side of the rock, and
    /// flies straight back in — which, next to a 400u asteroid, is a loop that never ends.
    /// </summary>
    private uint _escapeFrom;
    private DateTime _escapeSince = DateTime.MinValue;

    /// <summary>Rock we are roaming to with nothing better to do, so the log says so once.</summary>
    private uint _roamTarget;

    /// <summary>Whether the mining loop is currently stalled, so entering and leaving that state
    /// is logged once each instead of every tick.</summary>
    private bool _idle;

    private readonly Dictionary<uint, DateTime> _skip = new();

    /// <summary>Skips that <see cref="Roam"/> may not drop. See <see cref="SkipHard"/>.</summary>
    private readonly Dictionary<uint, DateTime> _hardSkip = new();
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

    /// <summary>Unanswered casts per rock since that rock last answered. Silence concentrated on
    /// one rock convicts the rock — it no longer exists — not the scanner.</summary>
    private readonly Dictionary<uint, int> _scanStrikes = new();

    /// <summary>The distinct rocks with any unanswered cast since the last reply from anyone.
    /// A dead consumable silences EVERY rock; one mute rock is the rock's own fault.</summary>
    private readonly HashSet<uint> _unansweredRocks = [];

    // Held-fire watchdog: how long MineTick has been engaged on one rock without a single shot
    // being possible — waiting for a scan, missing a known reach, parked outside every firing
    // band. One clock across all of those states, because they are one situation: engaged, not
    // firing, and nothing changing.
    private uint _holdId;
    private DateTime _holdSince;
    private DateTime _holdSeen;

    /// <summary>Whether we've already announced that filtering has been given up on because the
    /// scanner stopped answering. Cleared the moment a scan reply arrives.</summary>
    private bool _filterAbandoned;

    /// <summary>
    /// Whether the scanner is actually answering, as opposed to merely being cast.
    ///
    /// The server says nothing when it refuses a cast for want of a consumable, so a scanner out
    /// of power cells looks exactly like one that is working but slow. After this many casts with
    /// no reply at all, it is not slow.
    ///
    /// But only when the silence spans more than one rock. An empty consumable mutes every rock
    /// alike; casts swallowed by a single rock convict that rock — it is gone — and blaming the
    /// scanner for it tore the resource filter down every time the bot parked at a ghost.
    /// </summary>
    private bool ScannerAnswering =>
        _scansWithoutReply < ScanFailuresBeforeUnfiltered || _unansweredRocks.Count < 2;

    // ---- docking ---------------------------------------------------------------------
    private uint _dockTarget;
    private bool _docking;
    private DateTime _dockAsked = DateTime.MinValue;
    private DateTime _dockStarted = DateTime.MinValue;

    /// <summary>So the "not docking, and here is why" line is said once per retreat rather than
    /// four times a second for as long as the ship shelters there.</summary>
    private bool _dockDisabledSaid;

    /// <summary>The refuge we are currently parked at, and since when — the clock that decides a
    /// door is not going to open.</summary>
    private uint _dockTryId;
    private DateTime _dockTrySince = DateTime.MinValue;

    /// <summary>Objects that looked dockable, were flown to, and did not take us in. Per sector,
    /// because ids are.</summary>
    private readonly HashSet<uint> _dockRefused = [];

    /// <summary>Best hull fraction seen since arriving at the current refuge. The reference the
    /// "is being here working" test measures against.</summary>
    private float _refugeHullBest;

    /// <summary>Whether the ship is currently circling a refuge rather than parked at it.</summary>
    private bool _orbiting;

    /// <summary>
    /// Distance at which YOU last docked successfully. The real limit is the station's
    /// OwnerCard.DockRange, which isn't on the wire — but the server logs an outright cheat
    /// warning for docking from too far out, so we'd rather copy a distance that worked than
    /// guess one that might not.
    /// </summary>
    private float _learnedDockRange;

    // ---- death, repair and relaunch ---------------------------------------------------
    /// <summary>When the ship stopped being in the sector, or null while it is flying.</summary>
    private DateTime? _hangarSince;

    /// <summary>The death screen the server offered, waiting to be answered.</summary>
    private IReadOnlyList<(uint SectorId, uint CarrierPlayerId)>? _respawnOffer;

    /// <summary>The last death screen we were shown, kept after it was answered. A ship that will
    /// not launch is usually a ship the server still has dead, and this is the only thing that
    /// can say so again — the server does not repeat the offer.</summary>
    private IReadOnlyList<(uint SectorId, uint CarrierPlayerId)>? _lastRespawnOffer;

    private DateTime _respawnAnswered = DateTime.MinValue;
    private DateTime _lastLaunchAsk = DateTime.MinValue;
    private int _launchAsks;
    private bool _repairAsked;
    private DateTime _repairAskedAt = DateTime.MinValue;
    private float? _conditionBeforeRepair;
    private bool _repairWarned;

    /// <summary>True from the moment we were destroyed until the next launch. The repair is worth
    /// asking for even with no card to compare against, because dying always costs condition.</summary>
    private bool _diedHere;

    /// <summary>When you last asked to dock yourself. A removal that follows one of your own dock
    /// requests is you parking the ship, not the bot losing it — and the bot must not undo it.</summary>
    private DateTime _youDockedAt = DateTime.MinValue;

    public WeaponBook Weapons { get; } = new();

    /// <summary>
    /// The live server's own catalogue, built from the traffic it is already sending.
    ///
    /// Everything the bot has had to infer about a hull — how much armour it has, how fast it
    /// turns, what its slots are for — is stated outright in a card. Reading them is not extra
    /// knowledge smuggled in from elsewhere: it is the same source the client uses, taken from
    /// the same connection.
    /// </summary>
    public CatalogueSpy Cards { get; } = new();

    /// <summary>
    /// What actually happened in every fight, measured rather than assumed.
    ///
    /// The cards say what a hull is on paper; this says what it costs. Between them they are
    /// the two halves a time-to-kill decision needs, and neither is a constant borrowed from
    /// somebody else's server.
    /// </summary>
    public CombatLog Fights { get; } = new();

    /// <summary>Turns off the injected card requests, leaving passive sniffing alone. The
    /// requests are ordinary client traffic, but they are still traffic we invented.</summary>
    public bool FetchCatalogue { get; set; }

    public bool Enabled { get; private set; }
    public FarmMode Mode { get; set; } = FarmMode.Combat;

    // ---- tuning -------------------------------------------------------------------
    /// <summary>
    /// Last-resort reach, for a server that publishes neither slot stats nor a catalogue.
    ///
    /// Nearly dead, and deliberately so. Reach now comes from the live stat stream, then the
    /// catalogue, then what you declared — three real sources, of which the catalogue alone
    /// covers every fitted slot without you typing anything. This number is only consulted when
    /// all three are empty, and with <see cref="RequireKnownReach"/> on that case holds fire
    /// instead of shooting on a guess.
    ///
    /// It still sizes a couple of approach decisions, which is why it has not been deleted.
    /// </summary>
    public float FallbackRange { get; set; } = 3000f;

    /// <summary>
    /// Hold fire when a weapon's reach is unknown, rather than assuming
    /// <see cref="FallbackRange"/>.
    ///
    /// The asymmetry is the whole argument: an over-estimate makes the server refuse the cast
    /// without saying so — spent cooldown, spent power, silent failure, and in one case a
    /// scanner that looked like it was out of consumables when it was simply being aimed 1,000u
    /// beyond its reach. An under-estimate just flies closer than it needed to.
    /// </summary>
    public bool RequireKnownReach { get; set; } = true;

    /// <summary>
    /// How many one-shot abilities may be cast in a single 250ms tick.
    ///
    /// Not a throttle for its own sake. Firing every gun in one pass put nine casts on the wire
    /// inside three milliseconds, and bsgo.fun closed the connection immediately afterwards,
    /// repeatably. Whatever the server's exact rule, no human client produces that pattern.
    /// Two per tick is eight per second, which a person holding down the fire keys can match.
    /// </summary>
    public int MaxCastsPerTick { get; set; } = 2;

    /// <summary>Used when a weapon has no cooldown stat.</summary>
    public int FallbackFireIntervalMs { get; set; } = 900;

    /// <summary>Fly to targets that are out of range instead of just reporting them.</summary>
    public bool AutoApproach { get; set; } = true;

    /// <summary>Drop into the boost gear on long approaches. Costs tylium; the server puts you
    /// back in the regular gear by itself when the hold runs dry.</summary>
    public bool UseBoost { get; set; } = true;

    /// <summary>
    /// Seconds of boost-speed travel to leave for shedding the boost, on top of the braking zone.
    ///
    /// This replaces a flat 1,500u margin, which was asking the wrong question. "Am I far enough
    /// out to boost" is not a fixed distance, it is "is there room to run fast and still stop" —
    /// the braking zone plus the room it takes to come down off boost. The flat number was about
    /// three times the real requirement, so with a rock's standoff at ~170u nothing closer than
    /// 1,670u ever boosted, and no asteroid hop in a belt is that long. Every mining approach ran
    /// at cruise; the only boosts in a 48-minute session were two retreats across the sector.
    /// </summary>
    public float BoostShedSeconds { get; set; } = 1.5f;

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
    /// <summary>
    /// Which resources to mine, best first. Empty means "whatever is nearest".
    ///
    /// The order is the priority: while any rock of the first entry is confirmed and reachable,
    /// nothing further down the list is chosen. It only decides what to fly to NEXT, though — a
    /// rock already being worked is finished rather than abandoned the moment something
    /// higher-ranked respawns, because the damage already put into it would be thrown away.
    /// </summary>
    public List<ResourceType> WantedResources { get; } = [];

    /// <summary>
    /// True when a resource filter is actually narrowing anything.
    ///
    /// Ticking every minable resource is not a filter — it is the default written out longhand.
    /// Counting it as one was not harmless: the hold-fire rule in <see cref="MineAsync"/> parks
    /// the guns until a rock is identified, which buys nothing at all when every possible answer
    /// is one we accept. It only decided the order to fly to rocks in, and paid for that with a
    /// scan-shaped stall on every unscanned rock.
    /// </summary>
    private bool Filtering =>
        WantedResources.Count > 0 && !Array.TrueForAll(Resources.Minable, WantedResources.Contains);

    /// <summary>Whether a scanned rock holds something we asked for.</summary>
    private bool WantsResource(uint guid) =>
        !Filtering || WantedResources.Contains((ResourceType)guid);

    /// <summary>Also order a mining ship to the asteroid (costs resources) as well as
    /// firing your own mining laser.</summary>
    public bool UseMiningFacility { get; set; }

    /// <summary>How far inside the optimal range to sit. 0.6 means "60% of optimal" — close
    /// enough that drifting doesn't push us back out of the accurate band.</summary>
    public float CloseInFactor { get; set; } = 0.6f;

    /// <summary>Never try to sit closer than this. Flying into an object isn't an attack run.</summary>
    public float MinimumStandoff { get; set; } = 150f;

    /// <summary>
    /// The gap to leave between the ship and an asteroid's <b>surface</b>. The rock's own radius
    /// is added on top, so one number works for a 30u pebble and a 400u boulder alike.
    ///
    /// It used to be measured from the centre, which made it a setting you could not feel: it was
    /// floored by radius + margin, so on anything bigger than the number you typed the floor won
    /// and the value did nothing at all. 0 falls back to the derived standoff.
    ///
    /// Costs no accuracy to set low — the server's hit chance is flat at or below optimal range
    /// (HitchanceBasedOnThrottle.getChanceToHit) and only falls off beyond it.
    /// </summary>
    public float AsteroidStandoff { get; set; } = 120f;

    /// <summary>
    /// How far outside a body's clearance sphere to park, as a multiple of it.
    ///
    /// Exists because "just outside" and "exactly on the edge" behave completely differently: a
    /// ship parked on the boundary flips between "holding station" and "inside an obstacle" on
    /// noise alone.
    /// </summary>
    public float StandoffMargin { get; set; } = 1.15f;

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

    /// <summary>Clearance to add to an obstacle's own radius, in units, for everything that is
    /// not an asteroid or a planetoid — ships, stations, debris. The published radius is a
    /// bounding figure for the object, and says nothing about our own hull.</summary>
    public float CollisionMargin { get; set; } = 130f;

    /// <summary>
    /// Room to leave around an asteroid, on top of 90% of its radius — which is the collider the
    /// server actually builds for it.
    ///
    /// Deliberately small. Rocks are what the ship spends its life threading between, they are
    /// mostly tiny, and every unit here is added to a sphere the bot must stay out of, must brake
    /// for, and must steer around. Floored by our own hull radius, which is the only part of this
    /// that is genuinely non-negotiable.
    /// </summary>
    public float AsteroidCollisionMargin { get; set; } = 40f;

    /// <summary>
    /// Room to leave around a planetoid, on top of its scaled radius.
    ///
    /// Deliberately large, and for the opposite reason: there are a handful of them, none of them
    /// are on the way to anything, and the ship approaches them at cruise. A flat margin that
    /// suits a 40u rock is a rounding error on a 1,500u body.
    /// </summary>
    public float PlanetoidCollisionMargin { get; set; } = 500f;

    /// <summary>How much of a planetoid's published radius to treat as solid. Above 1 because
    /// nothing about a planetoid's stated size is conservative.</summary>
    public float PlanetoidClearanceFactor { get; set; } = 1.25f;

    /// <summary>How far ahead to look for obstacles, in seconds of travel at top speed. This has
    /// to cover the turn as well as the stop: the heading only updates a few times a second, so
    /// a deflection decided one ship-length out never lands.</summary>
    public float CollisionLookaheadSeconds { get; set; } = 5f;

    /// <summary>
    /// How long a continuous detour may hold the approach watchdog off before the target counts
    /// as unreachable after all.
    ///
    /// Generous enough to fly the long way around the largest body in a sector, short enough that
    /// a rock which cannot be reached — one tucked against a planetoid, or inside it — is given up
    /// on rather than orbited indefinitely.
    /// </summary>
    public float DetourPatienceSeconds { get; set; } = 45f;

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
    /// Unanswered casts at ONE rock before that rock is condemned as gone.
    ///
    /// The server answers a scan at any rock that exists and silently swallows a cast at one
    /// that doesn't, so silence concentrated on a single rock is evidence about the rock, not
    /// the scanner. Two casts a retry apart is ~40s of proof.
    /// </summary>
    public int ScanStrikesBeforeGone { get; set; } = 2;

    /// <summary>How long a rock condemned as unscannable or unworkable stays off the menu.
    /// Long, because the only cure is the server respawning the rock.</summary>
    public int MuteRockSkipMinutes { get; set; } = 30;

    /// <summary>
    /// How long the mining loop may sit engaged on one rock with no shot possible — waiting for
    /// a scan, no reach known for any gun, parked outside every firing band — before the rock is
    /// given up on. These holds used to have no clock at all: each returned before any watchdog
    /// armed, and the stall watchdog only counts time while the guns actually fire, so a rock
    /// that could never be worked parked the ship at full power indefinitely.
    /// </summary>
    public float HeldFirePatienceSeconds { get; set; } = 30f;

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
    ///
    /// Only consulted for an <b>area</b> scanner, where one cast identifies a whole field and a
    /// queue is a real thing to have. A single-target scanner earns one rock per cast, so it
    /// scans when there is nothing confirmed to shoot and stays quiet otherwise.
    /// </summary>
    public int ScanQueueDepth { get; set; } = 2;

    /// <summary>
    /// How long the guns may fire at a rock with nothing whatsoever to show for it before the
    /// rock is given up on.
    ///
    /// Neither its hull falling nor a single unit of ore reaching the hold, for this long, means
    /// we are not mining it — most often because it no longer exists. An asteroid we really are
    /// working reports one or the other within a second or two, so this can be short without ever
    /// firing on a rock that is merely tough.
    /// </summary>
    public float MiningStallSeconds { get; set; } = 20f;

    /// <summary>
    /// How old the server's last statement of where we are may be before a distance is no longer
    /// worth acting on.
    ///
    /// Only some messages carry our real position — SyncMove, the Rest/Teleport/Warp maneuvers, a
    /// WhoIs. A normal approach is Directional maneuvers, heading and march speed only, so in
    /// between the ship's position is integrated: flown straight, at the ordered speed, from the
    /// last fix. The real ship arcs through its turn and takes time to reach that speed, so the
    /// model runs ahead of it, and the error grows for as long as the flight lasts.
    ///
    /// That error is what parked the ship out of reach of a rock the status line said it was
    /// mining: the modelled distance crossed inside mining range, the throttle was cut, and the
    /// lasers fired at something the server considered too far away — silently, because an
    /// out-of-range cast is refused without a reply. Nothing recovered until the 20s stall
    /// watchdog gave up on a perfectly good rock.
    ///
    /// Four seconds because the model is accurate for a second or two, and <see
    /// cref="SpaceObj.PredictedPosition"/> stops advancing at three regardless.
    /// </summary>
    public float SelfPositionTrustSeconds { get; set; } = 4f;

    /// <summary>
    /// How long to sit still waiting for a fresh fix before flying on with the estimate anyway.
    ///
    /// Stopping is what asks the question: the ship coming to rest makes the server broadcast a
    /// Rest maneuver, which states a position outright. But a server that never sends one must
    /// not be able to park the bot indefinitely — a stationary ship's estimate stops drifting in
    /// any case, so past this point the estimate is the best there is and working on it beats
    /// waiting for something that isn't coming.
    /// </summary>
    public float SelfPositionWaitSeconds { get; set; } = 6f;

    /// <summary>
    /// The distance at which a rock is worth half its ore. Lower keeps the ship local; higher
    /// lets it range further for a big find. See <see cref="RockValue"/>.
    ///
    /// 1000 is set so that a rock at 2,000u needs five times the ore to be worth the trip — 3,000
    /// beats a nearby 500 — while one at 5,000u needs twenty-six times, which nothing realistically
    /// has. That is the line between "worth a detour" and "worth crossing the sector".
    /// </summary>
    public float RockTravelPenalty { get; set; } = 1000f;

    /// <summary>
    /// Scans cast with no reply at all before the resource filter is abandoned and the bot mines
    /// whatever it can reach. Higher than the 3 that triggers the "out of power cells" warning,
    /// so the warning always lands first and you get a chance to fix it.
    /// </summary>
    public int ScanFailuresBeforeUnfiltered { get; set; } = 5;

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

        Cards.Log += m => Log?.Invoke(m);
        _world.ObjectIdentified += OnObjectIdentified;

        Fights.Log += m => Log?.Invoke(m);
        _world.LoadoutChanged += DumpLoadoutOnce;
        // Names come from the catalogue, so a fight record reads "colonial_raider" rather than
        // a bare guid the moment the card has arrived.
        Fights.ClassOf = id =>
        {
            var o = _world.Get(id);
            if (o is null || o.CardGuid == 0) return (0u, "");
            return (o.CardGuid, Cards.World(o.CardGuid)?.PrefabName ?? "");
        };
        _world.ShotSeen += Fights.OnShot;
        _world.CombatSeen += ev => Fights.OnCombat(ev, ThrottleFraction);

        _world.Died += OnObjectDied;
        _world.LootOffered += OnLootOffered;
        _world.CastResult += OnCastResult;
        _world.AbilityStopped += OnAbilityStopped;
        _world.SectorLeft += OnSectorLeft;
        _world.RespawnOffered += OnRespawnOffered;
        _world.ShipConditionChanged += OnShipCondition;
        _world.AnchorChanged += OnAnchorChanged;
        _world.ScanReceived += OnScanReceived;
        _world.HoldGained += items => Meter.OnHoldGained(items, DateTime.UtcNow);

        Weapons.Learned += (w, isNew) =>
        {
            if (isNew) Log?.Invoke($"Learned weapon {w.Describe()}");
        };

        _timer = new System.Threading.Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);

        // Deliberately not on the farm timer. Learning what is in the sector is worth doing
        // whether or not the bot is flying the ship — and it must keep working while you fly
        // manually, which is exactly when the farm loop is stopped.
        _cardTimer = new System.Threading.Timer(_ => CardTick(), null,
                                                TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// Drains the card request queue and flushes the cache.
    ///
    /// Small batches on a slow clock on purpose. There is no hurry — a card is wanted before
    /// the fight, not during it — and the replies land in the real client too, so a burst would
    /// be a burst of work for it as well.
    /// </summary>
    private void CardTick()
    {
        if (Interlocked.Exchange(ref _cardBusy, 1) == 1) return;
        try
        {
            // Shares this timer rather than the farm loop for the same reason: a shot fired by
            // hand still needs resolving into a hit or a miss, and the farm loop is stopped
            // exactly when you are flying yourself.
            Fights.Tick();

            if (FetchCatalogue && _proxy.ClientConnected)
                Cards.DrainAsync(_act.RequestCards).GetAwaiter().GetResult();

            if ((DateTime.UtcNow - _lastCardSave).TotalSeconds > 30)
            {
                _lastCardSave = DateTime.UtcNow;
                Cards.SaveCache();
            }
        }
        catch
        {
            // A dropped session mid-request is ordinary; the queue keeps the entry and retries.
        }
        finally
        {
            Interlocked.Exchange(ref _cardBusy, 0);
        }
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

        // Pressing Go farm is also "try the scanner again". The dead-scanner verdict exists to
        // stop the bot wasting casts on its own; a person restarting the farm has had the
        // chance to refill cells or fix the loadout, and making them reconnect to clear a
        // verdict built on stale evidence is what made a fine scanner stay "broken".
        lock (_gate) _unansweredRocks.Clear();
        _scansWithoutReply = 0;
        _ammoWarned = false;

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
            _lootAsked.Clear(); _facilityOrdered.Clear(); _skip.Clear(); _hardSkip.Clear();
            _scanAsked.Clear(); _scanProbe.Clear(); _probed.Clear();
            _scanStrikes.Clear(); _unansweredRocks.Clear(); _dockRefused.Clear();
        }
        _roamTarget = 0;
        _scansWithoutReply = 0;
        _ammoWarned = false;
        _filterAbandoned = false;     // fresh session, so nothing has failed yet — no message

        // The catalogue survives a session — cards are per-server, not per-login — so it is
        // opened rather than cleared.
        //
        // The ship rosters are deliberately NOT requested here. They looked like the cheap way
        // to fill the table, being two guids, but each one names every hull in the game and each
        // hull cascades into its world, system and ability cards. That is thousands of replies,
        // all of which the real client also receives and parses. Cards for hulls we actually
        // meet arrive a couple at a time and cost nothing. Bulk is opt-in, via PrefetchRosters.
        Cards.OpenCache(_proxy.UpstreamKey);
    }

    /// <summary>
    /// Pull the entire ship catalogue in one go.
    ///
    /// Thousands of requests and replies, shared with the real client. Worth doing once on a
    /// quiet dock, not on every login, and not while flying.
    /// </summary>
    public void PrefetchRosters()
    {
        if (!FetchCatalogue)
        {
            Log?.Invoke("Card fetching is off — turn on \"Fetch cards\" first.");
            return;
        }
        Cards.WantShipRosters();
        Log?.Invoke("Requested both ship rosters. This will pull a large number of cards; "
                  + "expect the client to be busy for a while.");
    }

    private void OnSessionEnded()
    {
        Weapons.ResetToggles();
        Cards.SaveCache();
        // Only the in-flight bookkeeping: object ids do not survive a session, so pending shots
        // and per-attacker clocks would resolve against strangers. The per-class totals do
        // survive, because a class is the same class next time.
        Fights.Clear();
        lock (_gate) { _target = 0; _lockedTarget = 0; _subscribedTarget = 0; }
    }

    /// <summary>
    /// A WhoIs named a model. Ask for its card while it is still far away.
    ///
    /// Two rules, both learned the hard way:
    ///
    /// <b>Only ships get a Ship view.</b> Asking for a view a guid does not have is not a
    /// harmless miss — the server logs its own error and sends nothing, which reads to us as
    /// silence and gets retried. Mines were in this list and should never have been: a mine has
    /// a World card like everything else, and no Ship card at all.
    ///
    /// <b>World only for things that shoot at us</b>, which is why asteroids and planetoids are
    /// absent despite being the things we actually fly around. Their radius already arrives in
    /// the WhoIs body and is read straight into <c>SpaceObj.Radius</c>, so a card would buy
    /// nothing — and there are hundreds of rocks in a belt against a handful of hostiles, so
    /// asking for all of them is hundreds of requests the real client also has to swallow.
    /// </summary>
    private void OnObjectIdentified(uint objectId, uint cardGuid, SpaceEntityType type)
    {
        if (!FetchCatalogue) return;

        bool isShip = EntityTypes.IsShip(objectId);
        bool worthKnowing = isShip
            || type is SpaceEntityType.Mine or SpaceEntityType.SmartMine or SpaceEntityType.MineField;
        if (!worthKnowing) return;

        if (isShip) Cards.Want(cardGuid, CardView.Ship);
        Cards.Want(cardGuid, CardView.World);
    }

    private void OnSectorLeft(RemovingCause cause)
    {
        Weapons.ResetToggles();
        ForgetThrottle();

        // A fresh spell out of the sector: the relaunch sequence starts from here, whatever the
        // last one did. Note what is NOT reset — the death screen and the fact that we died. The
        // two messages arrive in whichever order the server sends them, and wiping a respawn
        // offer that landed first would leave the ship dead with nothing left to answer it.
        _hangarSince = DateTime.UtcNow;
        _launchAsks = 0;
        _lastLaunchAsk = DateTime.MinValue;
        _respawnAnswered = DateTime.MinValue;
        _repairAsked = false;
        _repairWarned = false;
        _conditionBeforeRepair = null;

        if (cause == RemovingCause.Death)
        {
            Deaths++;
            _diedHere = true;
            Log?.Invoke(AutoUndock
                ? $"Destroyed (death #{Deaths}). Waiting for the respawn options."
                : $"Destroyed (death #{Deaths}). Auto undock is off — respawn in the client.");
        }

        // You parked the ship yourself. Relaunching it would be the bot undoing an instruction,
        // so the farm stops instead and says why.
        bool yourDock = cause == RemovingCause.Dock
                     && !_docking
                     && (DateTime.UtcNow - _youDockedAt).TotalSeconds < 60;
        if (yourDock && Enabled)
        {
            Stop();
            Log?.Invoke("You docked by hand — farming stopped. Press Go farm after undocking.");
        }

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
            _scanStrikes.Clear(); _unansweredRocks.Clear();
            // The scanner-is-dead verdict goes with them. Every silent cast it was built on
            // aimed at a rock that no longer exists for us, and a hangar visit is exactly
            // where empty power cells get refilled — a verdict carried through a dock once
            // condemned a working scanner two casts into the next launch.
            _scansWithoutReply = 0;
            _ammoWarned = false;
            // A station that would not take us in is a fact about that station, and ids do not
            // survive a sector change.
            _dockRefused.Clear();
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
                // Cards the client asked for pass through here too, so the catalogue fills in
                // from the client's own browsing before we request anything ourselves.
                if (f.Protocol == ProtocolId.Catalogue
                    && (CatalogueOp.Reply)f.MsgType == CatalogueOp.Reply.Card)
                {
                    Cards.OnCardReply(r);
                    return;
                }

                // The docking countdown. The server answers a dock request with the delay it is
                // imposing — the client disables its DOCK button for that long and offers
                // CancelDocking instead (DockingButton.UpdateState). The bot knew the opcode and
                // did nothing with it, so it could not tell a countdown from silence.
                //
                // Read through its own reader: `r` is handed to the world model below, and a
                // half-consumed one would leave that reading from the middle of the message.
                if (f.Protocol == ProtocolId.Game
                    && (GameOp.Reply)f.MsgType == GameOp.Reply.DockingDelay)
                    NoteDockingDelay(f.Reader().ReadSingle());

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
                    OnClientGameMessage(f, r);
                    break;

                // What the hangar buttons actually send, printed as it happens. This is how the
                // undock sequence was pinned down rather than guessed: press UNDOCK and the log
                // states the message, in order, with nothing inferred.
                case ProtocolId.Room:
                    Log?.Invoke($"Client sent Room/{(RoomOp.Request)f.MsgType} ({f.MsgType}).");

                    // Room.Enter is the client loading a hangar, which it only does once the
                    // server has docked it. The server does not always announce that dock to us —
                    // one retreat docked cleanly, got no RemoveMe, and the bot spent the next
                    // half minute circling a station it was already inside of, steering by a
                    // position frozen at the moment of the dock. The client's own room load is
                    // the one signal that cannot be missing, so if the world still thinks we are
                    // flying when it lands, this IS the dock notification.
                    if ((RoomOp.Request)f.MsgType == RoomOp.Request.Enter
                        && (_world.MyObjectId != 0 || _world.MyPositionKnown))
                    {
                        Log?.Invoke("Room/Enter with the ship still in the world — the dock "
                                  + "succeeded without a RemoveMe. Treating it as docked.");
                        _world.Clear();
                        OnSectorLeft(RemovingCause.Dock);
                    }
                    break;
            }
        }
        catch
        {
            // Short or unfamiliar payloads are normal — never let parsing break the relay.
        }
    }

    private void OnClientGameMessage(FrameInfo f, BgoReader r)
    {
        NoteClientMessage(f.MsgType);

        switch ((GameOp.Request)f.MsgType)
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

            // The other half of the undock sequence, logged for the same reason as Room.Quit:
            // the client sends this itself once the space level has loaded, and seeing the two
            // land in order is the whole proof of how undocking works.
            case GameOp.Request.JumpIn:
                Log?.Invoke("Client sent Game/JumpIn (61) — its space level has finished loading.");
                break;

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

                // Only the real client's traffic reaches here — injected frames go straight to
                // the server — so this really is you pressing dock, not the bot's own dock run.
                _youDockedAt = DateTime.UtcNow;

                // Dumped raw, because every Dock the bot has sent itself was followed within
                // 400ms by the server hanging up, three times out of three, while the message
                // itself is byte-for-byte what GameProtocol.RequestDock writes. Something ELSE
                // about a real dock differs, and the only way to find out what is to read one.
                //
                // The whole frame, not just this message: the client batches everything queued in
                // a tick into one frame, so if a dock is really "LockTarget then Dock" — which is
                // what SpaceLevel.Dock implies, since it docks GetPlayerTarget() — the proof is
                // in the other messages sitting beside it.
                DumpDockFrame(f);

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
        lock (_gate) { _scanStrikes.Remove(asteroidId); _unansweredRocks.Clear(); }
        if (_filterAbandoned) { _filterAbandoned = false; Log?.Invoke("Scanner is answering again — resource filtering is back on."); }

        // An answer is proof of reach: the server drops scan targets outside the ability's own
        // MaxRange without saying anything, so anything it DID answer was inside it. This is the
        // only honest measurement of a reach nothing publishes — and it grows on its own, so a
        // scanner that turns out to reach 2,000u is used at 2,000u without anyone typing that in.
        if (_world.Get(asteroidId) is { } rock && _world.DistanceToMe(rock) is { } d
            && d > _scanProvenRange)
        {
            float was = _scanProvenRange;
            _scanProvenRange = d;
            if (Scanner()?.MaxRange is not > 0 && d > was * 1.2f)
                Log?.Invoke($"Scan answered from {d:F0}u — using that as the scanner's proven reach.");
        }

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

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
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
            NoteTickCost(System.Diagnostics.Stopwatch.GetElapsedTime(started));
        }
    }

    /// <summary>Worst and mean farm tick since the last report, for the diagnostics panel.</summary>
    public double SlowestTickMs { get; private set; }
    public double MeanTickMs { get; private set; }

    private double _tickTotalMs;
    private int _tickCount;
    private DateTime _tickReportedAt = DateTime.UtcNow;

    /// <summary>
    /// Times the farm tick, and says so when it overruns.
    ///
    /// Added because two separate performance theories were argued from behaviour rather than
    /// from a number. The tick has a 250ms budget; anything approaching that is starving the
    /// message pump it shares a machine with, and anything past it means ticks are being dropped
    /// outright. Reported at most once every 10 seconds so the cure is not another log flood.
    /// </summary>
    private void NoteTickCost(TimeSpan elapsed)
    {
        double ms = elapsed.TotalMilliseconds;
        _tickTotalMs += ms;
        _tickCount++;
        if (ms > SlowestTickMs) SlowestTickMs = ms;

        var now = DateTime.UtcNow;
        if ((now - _tickReportedAt).TotalSeconds < 10) return;

        MeanTickMs = _tickCount > 0 ? _tickTotalMs / _tickCount : 0;
        if (SlowestTickMs > 100)
            Log?.Invoke($"Farm tick is slow — {MeanTickMs:F0}ms mean, {SlowestTickMs:F0}ms worst "
                      + $"over {_tickCount} tick(s), against a 250ms budget.");

        _tickReportedAt = now;
        _tickTotalMs = 0;
        _tickCount = 0;
        SlowestTickMs = 0;
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

            // Cheap, and the only source of real numbers on a server that publishes no slot
            // stats. Runs on the same clock so a card arriving late still lands.
            int learned = Weapons.RefreshFromCatalogue(_world, Cards);
            if (learned > 0)
                Log?.Invoke($"Learned {learned} slot(s) from the catalogue — ranges, reload, "
                          + "power and role, with nothing typed in.");
        }

        // Not in the sector: dead, docked, or jumping. Getting back out is its own sequence and
        // it runs above every flying decision, because none of them apply to a ship in a hangar.
        if (await HangarTickAsync()) return;

        // In the sector but not flying: riding a carrier. Above everything for the same reason,
        // and it is the stricter case — a hangar cannot be steered into an outpost, a carrier can.
        if (await AnchorTickAsync()) return;

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

        // When we were last under way, which is the only thing that can put our idea of where we
        // are out of step with the server's. Sampled here rather than in the mining loop because
        // the flight that causes the drift is just as likely to have been a chase or a dock run.
        if (_throttleOpen || _world.MyVelocity.LengthSquared() > 1f)
            _movedAt = DateTime.UtcNow;

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

        // Out of the retreat, so the next one gets to explain itself again.
        _dockDisabledSaid = false;

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
        Status = $"{how} {target} — {dist:F0}u, stopping at {hold:F0}u, {SpeedInGear(_gear):F0}u/s {_gear}";
    }

    // ------------------------------------------------------------------ docking

    /// <summary>
    /// Whether the bot may send a dock request at all.
    ///
    /// On, now that the sequence is right. It was briefly off for good reason: three dock
    /// requests had ever been sent — 02:18:54, 02:34:54 and 13:37:15 on 27 Jul — and the server
    /// hung up 78ms, 364ms and 80ms later, with no case of one working.
    ///
    /// Neither the message nor the range was ever the problem. A real dock captured off the wire
    /// is <c>022D000100004A00000000</c>, which is byte-for-byte what the bot sent, and the same
    /// outpost accepted a manual dock from 791u while the bot was asking from 248u. What was
    /// missing was the LockTarget in front of it — see <see cref="LockBeforeDockAsync"/>.
    ///
    /// Kept as a switch because it is the one action with a proven history of ending sessions.
    /// Turning it off costs nothing but the last step: the retreat still runs to the outpost and
    /// shelters under its guns, which is the part that saves the ship.
    /// </summary>
    public bool AllowDocking { get; set; } = true;

    /// <summary>Until when the server says a docking countdown is running, from Reply.DockingDelay.
    /// The client disables its dock button for exactly this long, so a second request inside the
    /// window is something the real client can never send.</summary>
    private DateTime _dockCountdownUntil = DateTime.MinValue;

    private bool DockCountdownRunning => DateTime.UtcNow < _dockCountdownUntil;

    /// <summary>
    /// Records the docking countdown the server just imposed.
    /// </summary>
    private void NoteDockingDelay(float seconds)
    {
        _dockCountdownUntil = DateTime.UtcNow.AddSeconds(Math.Max(0f, seconds));
        Log?.Invoke($"Server answered with a docking countdown of {seconds:F1}s — "
                  + "holding off any further dock request until it runs out.");
    }

    /// <summary>
    /// Prints one of YOUR dock requests exactly as it left the client.
    ///
    /// Both halves matter. The frame hex settles whether our own dock message is wrong at the
    /// byte level. The message list settles the likelier question: the client only ever docks
    /// <c>GetPlayerTarget()</c>, so a real dock may well be a LockTarget followed by a Dock, and
    /// the bot's retreat clears its target before asking.
    /// </summary>
    private void DumpDockFrame(FrameInfo f)
    {
        Log?.Invoke($"YOUR DOCK — raw frame, {f.Payload.Length}b: {Convert.ToHexString(f.Payload)}");

        var parts = MessageSplitter.Split(f.Payload, fromClient: true);
        Log?.Invoke($"YOUR DOCK — that frame holds {parts.Count} message(s): "
                  + string.Join(" + ", parts.Select(m =>
                        $"{(GameOp.Request)m.MsgType}({m.MsgType}) {m.BodyLength}b")));

        (DateTime At, ushort Type)[] recent;
        lock (_gate) recent = _clientTrail.ToArray();
        if (recent.Length > 0)
        {
            var now = DateTime.UtcNow;
            Log?.Invoke("YOUR DOCK — what the client sent in the seconds before it: "
                      + string.Join(", ", recent.Select(e =>
                            $"-{(now - e.At).TotalSeconds:F1}s {(GameOp.Request)e.Type}")));
        }
    }

    /// <summary>The last few Game requests the real client sent, so a dock can be read in
    /// context rather than in isolation. Bounded and cheap: two fields per entry.</summary>
    private readonly Queue<(DateTime At, ushort Type)> _clientTrail = new();

    private void NoteClientMessage(ushort msgType)
    {
        lock (_gate)
        {
            _clientTrail.Enqueue((DateTime.UtcNow, msgType));
            while (_clientTrail.Count > 16) _clientTrail.Dequeue();
        }
    }

    /// <summary>When we locked the station we are about to dock.</summary>
    private DateTime _dockLockedAt = DateTime.MinValue;

    /// <summary>How long to let a lock settle before docking on the back of it. One tick would
    /// probably do; this is a couple, because the whole point is to stop racing the server.</summary>
    private const double DockLockSettleMs = 600;

    /// <summary>
    /// Selects the station, the way a player does, and says whether the dock must still wait.
    ///
    /// This is the fix for the three sessions that ended within 400ms of a dock request. The
    /// message we sent was byte-for-byte identical to the one the client sends — proven by
    /// dumping a real one: both are <c>022D000100004A00000000</c> — and the range was fine, since
    /// the same outpost accepted a manual dock from 791u while the bot was asking from 248u.
    ///
    /// What differed was everything around it. <c>SpaceLevel.Dock()</c> can only ever dock
    /// <c>GetPlayerTarget()</c>, so a real dock is always a LockTarget followed by a Dock — and
    /// the captured trail shows exactly that, a LockTarget for the outpost twenty seconds ahead of
    /// the dock. The retreat, meanwhile, cleared its target as its first act and asked to dock a
    /// station the server had never been told we had selected. That is a request no client can
    /// produce, and this server answers it by hanging up rather than refusing.
    ///
    /// Returns true while the caller should hold off.
    /// </summary>
    private async Task<bool> LockBeforeDockAsync(SpaceObj station)
    {
        if (_lockedTarget != station.Id)
        {
            await EnsureLocked(station.Id);

            // EnsureLocked declines to lock something the client could not see. Docking on the
            // back of a lock that never went out is the very thing this exists to prevent, so
            // that case waits rather than falling through.
            if (_lockedTarget != station.Id)
            {
                WarnOnce($"cannot lock {station} to dock at it — the lock was refused.");
                return true;
            }

            _dockLockedAt = DateTime.UtcNow;
            lock (_gate) _target = station.Id;
            Log?.Invoke($"Selected {station} — locking before the dock request, which is the "
                      + "order the client itself uses.");
            return true;
        }

        return (DateTime.UtcNow - _dockLockedAt).TotalMilliseconds < DockLockSettleMs;
    }

    /// <summary>How close to get before asking to dock, when nothing has been learned yet.</summary>
    public float DockApproach { get; set; } = 250f;

    /// <summary>Give up on a dock run after this long.</summary>
    public int DockTimeoutSeconds { get; set; } = 90;

    /// <summary>
    /// The shortest time we will spend at a refuge before its hull trend is allowed to send us
    /// away. Hysteresis, not a deadline: one burst of damage on arrival must not abandon an
    /// outpost that is about to let us in.
    /// </summary>
    public float DockGiveUpSeconds { get; set; } = 10f;

    /// <summary>
    /// How much hull we will lose at a refuge before deciding it is not sheltering us.
    ///
    /// This replaced a flat 10s timeout, which was wrong in exactly the case it was written for:
    /// a dock cooldown after combat can run to tens of seconds, and a short timer abandons a good
    /// outpost while its countdown is still ticking. Whether the shelter is working is a thing we
    /// can measure — 0.10 is ten points of hull lost since the best reading since arriving.
    /// </summary>
    public float RefugeBleedFraction { get; set; } = 0.10f;

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

    /// <summary>
    /// Get out — of a hangar OR of a carrier, which are different messages.
    ///
    /// The client's UNDOCK button tests anchoring FIRST and only reaches <c>Room.Quit</c> if it
    /// is not set (<c>UndockButton.Undock</c>). Sending the hangar message while riding a carrier
    /// is the wrong message for the state, which is where this started.
    /// </summary>
    public void Undock()
    {
        _docking = false;
        _dockTarget = 0;
        _lastLaunchAsk = DateTime.UtcNow;

        if (_world.Anchored)
        {
            _ = _act.RequestUnanchor();
            Status = "Launching from the carrier";
            Log?.Invoke($"Unanchor requested — riding #{_world.AnchoredTo:X8}.");
            return;
        }

        _ = _act.LeaveRoom();
        Status = "Undocking";
        Log?.Invoke("Undock requested (Room.Quit).");
    }

    // ------------------------------------------------------------------ death & relaunch

    /// <summary>
    /// Get the ship back out of the hangar by itself: answer the death screen, buy the hull
    /// condition back, launch, and carry on farming.
    ///
    /// Off means a death ends the session in every practical sense — the farm loop keeps ticking
    /// against a ship that is not in the sector and does nothing at all until someone presses
    /// Undock.
    /// </summary>
    public bool AutoUndock { get; set; } = true;

    /// <summary>Buy the ship's condition back before launching, with titanium. Dying always costs
    /// condition, and a wrecked hull launches with a fraction of its stats.</summary>
    public bool AutoRepair { get; set; } = true;

    /// <summary>
    /// How long to sit in the hangar before launching.
    ///
    /// Not politeness: the client has its own death sequence to play out, the repair has to be
    /// asked for and answered, and the server will not launch a ship it still thinks is dead. Six
    /// seconds is enough for all three without making a death cost a minute of farming.
    /// </summary>
    public int UndockDelaySeconds { get; set; } = 6;

    /// <summary>How long to wait before asking to launch again when the first ask changed nothing.</summary>
    public int RelaunchIntervalSeconds { get; set; } = 15;

    /// <summary>Times we've been destroyed this session.</summary>
    public int Deaths { get; private set; }

    /// <summary>Times the bot has bought the hull back this session.</summary>
    public int RepairsBought { get; private set; }

    /// <summary>True while the ship is out of the sector — dead, docked, or jumping.</summary>
    public bool InHangar => _hangarSince is not null;

    /// <summary>Condition of the ship we're flying against what its card says it should be, or
    /// null while either half is unknown.</summary>
    public (float Now, float Max)? Condition
    {
        get
        {
            if (_world.MyCondition is not { } now) return null;
            uint guid = _world.MyShipGuid;
            if (guid == 0 || Cards.Ship(guid)?.Durability is not { } max || max <= 0) return null;
            return (now, max);
        }
    }

    /// <summary>Whether the hull is worth paying to patch. Null when we cannot tell — no card, or
    /// no ShipInfo — which is a different answer from "no".</summary>
    private bool? ConditionShort() =>
        Condition is { } c ? c.Now < c.Max * 0.999f : null;

    private void OnRespawnOffered(IReadOnlyList<(uint SectorId, uint CarrierPlayerId)> options)
    {
        _respawnOffer = options;
        _lastRespawnOffer = options;
        _diedHere = true;
        Log?.Invoke($"Death screen: {options.Count} respawn location(s) offered"
                  + (AutoUndock ? "." : " — auto undock is off, so pick one in the client."));
    }

    private void OnShipCondition(ushort shipId, float durability)
    {
        if (shipId != _world.MyShipId || !_repairAsked) return;

        // The server answering a repair is the only proof it took it. Comparing against what we
        // saw before we asked keeps a routine ShipInfo from being read as a successful repair.
        if (_conditionBeforeRepair is { } was && durability > was + 0.01f)
        {
            RepairsBought++;
            _repairWarned = true;                 // it worked; nothing to warn about
            string of = Condition is { } c ? $" of {c.Max:F0}" : "";
            Log?.Invoke($"Repaired: condition {was:F0} → {durability:F0}{of}.");
        }
    }

    /// <summary>Forget the whole hangar sequence. Called once the ship is flying again.</summary>
    private void ClearHangarState()
    {
        _hangarSince = null;
        _respawnOffer = null;
        // Dropped with the rest: a death screen from an earlier death must never be answered
        // during an ordinary dock later on.
        _lastRespawnOffer = null;
        _respawnAnswered = DateTime.MinValue;
        _launchAsks = 0;
        _lastLaunchAsk = DateTime.MinValue;
        _repairAsked = false;
        _repairWarned = false;
        _conditionBeforeRepair = null;
        _diedHere = false;
    }

    /// <summary>True while we are riding a carrier rather than flying our own ship.</summary>
    public bool IsAnchored => _world.Anchored;

    private DateTime? _anchoredSince;
    private DateTime _lastUnanchorAsk = DateTime.MinValue;
    private int _unanchorAsks;

    /// <summary>Wait this long after anchoring before asking to launch, so a carrier you boarded
    /// on purpose is not immediately thrown off it.</summary>
    public int UnanchorDelaySeconds { get; set; } = 4;

    private void OnAnchorChanged(uint carrier)
    {
        if (carrier != 0)
        {
            _anchoredSince = DateTime.UtcNow;
            _lastUnanchorAsk = DateTime.MinValue;
            _unanchorAsks = 0;

            // Everything we might have had running belongs to a ship we are no longer flying.
            Weapons.ResetToggles();
            ForgetThrottle();
            if (_docking) { _docking = false; _dockTarget = 0; }
            lock (_gate) { _target = 0; _lockedTarget = 0; }

            var owner = _world.Get(carrier);
            Log?.Invoke($"Anchored to {(owner is not null ? owner.ToString() : $"#{carrier:X8}")}"
                      + " — riding, not flying. No steering, no firing, no docking until we launch.");
            return;
        }

        _anchoredSince = null;
        _unanchorAsks = 0;
        Log?.Invoke("Off the carrier — flying our own ship again.");
    }

    /// <summary>
    /// The carrier state. Blocks the farm loop outright and, when farming, asks to launch.
    ///
    /// Blocking is the important half. While anchored the client disables its whole ability bar
    /// and every flight control, because the ship is a passenger — so throttle, heading, casts and
    /// dock requests are all traffic no real client can produce in that state. The bot sent them
    /// anyway, for six seconds, ending in a Dock request from inside somebody's Brimir; the server
    /// closed the connection on the spot.
    ///
    /// Returns true whenever we are anchored, so nothing downstream gets the tick.
    /// </summary>
    private async Task<bool> AnchorTickAsync()
    {
        if (!_world.Anchored) return false;

        var now = DateTime.UtcNow;
        _anchoredSince ??= now;

        var carrier = _world.Get(_world.AnchoredTo);
        string riding = carrier is not null ? carrier.ToString() : $"#{_world.AnchoredTo:X8}";

        if (!AutoUndock || !Enabled)
        {
            Status = $"Anchored to {riding} — riding along";
            return true;
        }

        double waited = (now - _anchoredSince.Value).TotalSeconds;
        if (waited < UnanchorDelaySeconds)
        {
            Status = $"Anchored to {riding} — launching in {UnanchorDelaySeconds - waited:F0}s";
            return true;
        }

        if ((now - _lastUnanchorAsk).TotalSeconds >= RelaunchIntervalSeconds)
        {
            _lastUnanchorAsk = now;
            _unanchorAsks++;
            await _act.RequestUnanchor();
            Log?.Invoke(_unanchorAsks == 1
                ? $"Launching from {riding} (RequestUnanchor) to carry on farming."
                : $"Still aboard {riding} — asking to launch again (attempt {_unanchorAsks}).");
        }

        Status = $"Launching from {riding}"
               + (_unanchorAsks > 1 ? $" — asked {_unanchorAsks}x" : "");
        return true;
    }

    /// <summary>
    /// The whole out-of-sector state machine, in the order the server needs it: answer the death
    /// screen, repair, then launch — and keep asking to launch, because the first ask can land
    /// while the server still has us dead.
    ///
    /// Returns true when it has taken the tick, i.e. the ship is not in the sector and the bot is
    /// doing something about it.
    /// </summary>
    private async Task<bool> HangarTickAsync()
    {
        bool inSector = _world.MyObjectId != 0 && _world.MyPositionKnown;
        if (inSector)
        {
            if (_hangarSince is { } since)
            {
                Log?.Invoke($"Flying again after {(DateTime.UtcNow - since).TotalSeconds:F0}s out of the sector.");

                // A death does not always arrive as RemoveMe — the ship can leave as a plain
                // ObjectLeft, in which case OnSectorLeft never ran and every piece of in-flight
                // bookkeeping survived the grave. The approach watchdog proved it: a
                // best-distance from the previous life plus a respawn somewhere else read as
                // "no progress for 38s", and a perfectly good rock was skipped the same second
                // the new ship entered space. This is the new ship's first tick: whatever the
                // old one was doing, it is not doing it any more. World knowledge — rocks,
                // scans, skips — is still true and stays.
                Weapons.ResetToggles();
                ForgetThrottle();
                _mineWatchId = 0;
                _holdId = 0;
                _fixWaitSince = DateTime.MinValue;
                _fixWaitGaveUp = false;
                _movedAt = DateTime.MinValue;
                // Locks and subscriptions belong to the dead ship; force both to be re-sent.
                lock (_gate) { _lockedTarget = 0; _subscribedTarget = 0; }

                ClearHangarState();
            }
            return false;
        }

        // Before the login handshake there is no ship to launch and no hangar to launch it from —
        // that is the "waiting for the handshake" case, not a docked one.
        if (_world.MyPlayerId == 0) return false;

        var now = DateTime.UtcNow;
        _hangarSince ??= now;

        if (!AutoUndock || !Enabled) return false;

        // A death screen blocks everything else: the server will not launch a dead ship, and
        // nothing else answers this message once the bot is flying.
        if (_respawnOffer is { Count: > 0 } offer)
        {
            // A station, not a stranger's carrier. A carrier id of 0 means the option is a place
            // of our own; anything else lands us anchored inside another player's ship, which is
            // a state the bot cannot farm from, cannot leave without their say-so, and used to
            // fly around inside. Taking offer[0] blindly is how we ended up in a Brimir.
            var pick = offer.FirstOrDefault(o => o.CarrierPlayerId == 0, offer[0]);

            _respawnOffer = null;
            _respawnAnswered = now;
            await _act.SelectRespawnLocation(pick.SectorId, pick.CarrierPlayerId);
            Log?.Invoke($"Respawning at sector {pick.SectorId}"
                      + (pick.CarrierPlayerId != 0
                            ? $" (carrier of player {pick.CarrierPlayerId} — no station was offered)"
                            : "")
                      + $" — {offer.Count} location(s) were offered.");
            Status = "Respawning";
            return true;
        }

        // Give the respawn a moment to land before asking the hangar for anything.
        if (_respawnAnswered != DateTime.MinValue && (now - _respawnAnswered).TotalSeconds < 2)
        {
            Status = "Respawning";
            return true;
        }

        if (await RepairInHangarAsync(now)) return true;

        double waited = (now - _hangarSince.Value).TotalSeconds;
        if (waited < UndockDelaySeconds)
        {
            Status = $"In the hangar — launching in {UndockDelaySeconds - waited:F0}s";
            return true;
        }

        // Three launches ignored means the server is not refusing to undock us — it still has us
        // dead, and JumpIn is simply the wrong message. Answer the death screen again.
        if (_launchAsks >= 3 && _lastRespawnOffer is { Count: > 0 }
            && (now - _respawnAnswered).TotalSeconds > 45)
        {
            _respawnOffer = _lastRespawnOffer;
            _launchAsks = 0;
            Log?.Invoke("Three launches changed nothing — answering the death screen again.");
            return true;
        }

        if ((now - _lastLaunchAsk).TotalSeconds >= RelaunchIntervalSeconds)
        {
            _lastLaunchAsk = now;
            _launchAsks++;
            await _act.LeaveRoom();

            // The client sends its own JumpIn once the space level has loaded, so ours is only
            // worth trying after the room has plainly already been left and we are stuck at the
            // last step instead of the first.
            if (_launchAsks >= 2) await _act.JumpIn();

            Log?.Invoke(_launchAsks == 1
                ? "Undocking to carry on farming (Room.Quit)."
                : $"Still in the hangar — Room.Quit and JumpIn again (attempt {_launchAsks}).");
        }

        Status = _launchAsks > 1
            ? $"Undocking — asked {_launchAsks}x, {(now - _hangarSince.Value).TotalSeconds:F0}s in the hangar"
            : "Undocking";
        return true;
    }

    /// <summary>
    /// Buys the hull back before launching. One RepairAll covers the hull and every fitted system,
    /// which is what a death damages — repairing the hull alone launches a ship with dead slots.
    ///
    /// Returns true while it wants the tick to itself, i.e. it just asked and is waiting.
    /// </summary>
    private async Task<bool> RepairInHangarAsync(DateTime now)
    {
        if (!AutoRepair || _world.MyShipId == 0) return false;

        if (!_repairAsked)
        {
            // Repair when we know it is short, or when we died — dying always costs condition,
            // and on a server that sends no ShipInfo that is the only signal there is.
            bool? shortOf = ConditionShort();
            if (shortOf == false || (shortOf is null && !_diedHere)) return false;

            _repairAsked = true;
            _repairAskedAt = now;
            _conditionBeforeRepair = _world.MyCondition;

            // Titanium, never cubits. Cubits are bought with money, and nothing the bot does by
            // itself should be able to spend them.
            await _act.RepairAll(_world.MyShipId, useCubits: false);
            Log?.Invoke(Condition is { } c
                ? $"Repairing ship {_world.MyShipId} — condition {c.Now:F0}/{c.Max:F0}, paying titanium."
                : $"Repairing ship {_world.MyShipId} with titanium (condition unknown).");
            Status = "Repairing";
            return true;
        }

        // Asked, and the server never moved the number. Say so once: the likely causes are no
        // titanium, a hull the server only repairs for cubits, or a server that ignores RepairAll.
        if (!_repairWarned && (now - _repairAskedAt).TotalSeconds > 8)
        {
            _repairWarned = true;
            if (ConditionShort() != false)
                Log?.Invoke("Repair didn't take — the server left the condition where it was. "
                          + "Check titanium, or repair by hand in the damage window.");
        }

        return false;
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

        // A countdown the server itself imposed is not the run failing, it is the run working —
        // so the timeout stands down while one is ticking. A dock delay after combat can be tens
        // of seconds, which would otherwise abandon a dock that was about to complete.
        if (!DockCountdownRunning && (DateTime.UtcNow - _dockStarted).TotalSeconds > DockTimeoutSeconds)
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
            Status = $"Docking — {dist:F0}u to {station}, closing to {ask:F0}u, {SpeedInGear(_gear):F0}u/s {_gear}";
            return;
        }

        await StopThrottleIfMoving();

        // Arrived, but the request itself is the dangerous part — see AllowDocking. The run ends
        // here rather than pretending to continue, because the ship is where it was asked to be.
        if (!AllowDocking)
        {
            _docking = false;
            Status = $"At {station} ({dist:F0}u) — not docking, it drops the session";
            Log?.Invoke($"Arrived at #{station.Id:X8} ({dist:F0}u) but did not send a dock "
                      + "request: every one the bot has sent ended the session within 400ms. "
                      + "Dock by hand — the bot reads your request and learns from it — or set "
                      + "AllowDocking in bot.json.");
            return;
        }

        // Selected first, exactly as the client does — see LockBeforeDockAsync.
        if (await LockBeforeDockAsync(station))
        {
            Status = $"At {station} ({dist:F0}u) — selecting it to dock";
            return;
        }

        // Once per few seconds, not per tick: every rejected attempt writes a line in the
        // server log with your player id. And never inside the server's own countdown, which
        // disables the real client's dock button for its duration.
        if (DockCountdownRunning) return;
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
                       + $", {SpeedInGear(_gear):F0}u/s {_gear}";
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

        if (!ClientCanSee(o.Id)) return false;

        return _world.RelationTo(o.Id) is Relation.Enemy or Relation.Neutral;
    }

    /// <summary>
    /// Restrict hunting, locking and firing to contacts inside your own detection radii.
    ///
    /// <b>Off</b>, which is how the bot has always behaved: WhoIs reports objects far beyond
    /// every detection ring, so the bot happily engages things the client draws nothing for.
    /// That was suspected of causing the combat disconnects and turned on — but the theory was
    /// never actually tested, because the run that failed next was still the previous build. So
    /// it goes back to off rather than staying on unearned.
    ///
    /// Worth revisiting on its own merits: engaging a contact 9,000u away with a 1,500u DRADIS
    /// is a long flight to something that may not be there by the time we arrive.
    /// </summary>
    public bool HuntOnlyVisible { get; set; }

    // ------------------------------------------------------------------ hostile emplacements

    /// <summary>A gun that doesn't move: weapon platform or outpost.</summary>
    private static bool IsEmplacement(SpaceObj o) =>
        o.Type is SpaceEntityType.WeaponPlatform or SpaceEntityType.Outpost;

    /// <summary>
    /// Enemy emplacements we know the position of. Neutral ones are left out: RelationTo reports
    /// Neutral whenever either side is factionless, which covers plenty of things that never
    /// shoot at anybody.
    /// </summary>
    private readonly Lock _stationGate = new();
    private List<SpaceObj>? _stationCache;
    private DateTime _stationCacheAt;

    /// <summary>
    /// Enemy emplacements in the sector, rebuilt at most a few times a second.
    ///
    /// The cache is not an optimisation, it is a bug fix. This used to take a full
    /// <see cref="WorldState.Snapshot"/> — a deep copy of every object in the sector — on every
    /// single call, and its only caller is <see cref="InStationDanger"/>, which is asked about
    /// ONE OBJECT AT A TIME from inside predicates that are themselves run over every object:
    /// <see cref="MiningCandidate"/>, <see cref="CombatCandidate"/>, <see cref="Roam"/>.
    ///
    /// So a single "which rock should I mine" pass over 200 contacts did 200 full world copies,
    /// each holding the world lock. The farm tick and the UI's diagnostics tick both run at
    /// 250ms, so that was happening eight times a second — enough to peg the UI thread solid
    /// (the window could not be dragged or raised) and to starve the relay's decode thread of
    /// the world lock until the server gave up on us and closed the connection.
    ///
    /// Stations do not move and rarely change, so half a second of staleness costs nothing.
    /// </summary>
    private List<SpaceObj> HostileStations()
    {
        if (!AvoidHostileStations) return [];

        var now = DateTime.UtcNow;
        lock (_stationGate)
        {
            if (_stationCache is not null && (now - _stationCacheAt).TotalMilliseconds < 500)
                return _stationCache;
        }

        var fresh = _world.Snapshot()
            .Where(o => IsEmplacement(o) && o.HasPosition && _world.RelationTo(o.Id) == Relation.Enemy)
            .ToList();

        lock (_stationGate)
        {
            _stationCache = fresh;
            _stationCacheAt = now;
        }
        return fresh;
    }

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

    /// <summary>
    /// The resource scanner to actually use.
    ///
    /// There can be more than one candidate, and picking the wrong one is silent and total. A
    /// scanner learned from a scan reply — the bot watching a cast and an answer land together —
    /// carries no reach at all, while the one you declared in the loadout panel carries the range
    /// you typed. Taking whichever came first out of the dictionary is how ability #7, guessed
    /// and rangeless, shadowed the declared #9 with its 2,000u reach: every scan was then refused
    /// for want of a range that was sitting right there on the other entry.
    ///
    /// So: what you declared beats what was guessed, and a known reach beats an unknown one.
    /// </summary>
    private Weapon? Scanner() =>
        Weapons.For(WeaponRole.Scanner)
            .OrderByDescending(w => w.RoleFromUser)
            .ThenByDescending(w => w.MaxRange ?? 0f)
            .FirstOrDefault();

    /// <summary>
    /// How far to scan when nothing has published the scanner's reach.
    ///
    /// Deliberately short. The server checks the scan against the ability's own
    /// <c>ObjectStat.MaxRange</c> and simply skips targets beyond it without a word
    /// (bsgocore <c>ResourceScanAction.internalProcess</c>), so overstating the reach produces
    /// silent nothing — which is exactly what a 3,000u guess did.
    /// </summary>
    public float ScanReachFallback { get; set; } = 600f;

    /// <summary>The furthest a scan has ever actually been ANSWERED from. Measured, so it beats
    /// every guess: the server only replies for targets inside the ability's real reach.</summary>
    private float _scanProvenRange;

    /// <summary>
    /// The radius to scan within.
    ///
    /// Three sources, best first. The published stat if there is one. Otherwise the furthest a
    /// scan has actually been answered from, with a little headroom to keep probing outwards —
    /// that number can only grow from replies, so it can never overstate the reach by more than
    /// the headroom. Failing both, a short fallback floored by the mining reach: the rock we are
    /// about to mine is by definition within the lasers' range, so a scanner that cannot manage
    /// that distance cannot usefully filter anything anyway.
    ///
    /// This replaces refusing to scan at all. The refusal was right about the guess — 3,000u was
    /// invented — but wrong about the conclusion: a measured distance is not a guess, and no
    /// scanning at all means no filtering at all, which is what left the bot breaking every rock
    /// it passed regardless of what you asked for.
    /// </summary>
    private float ScanReach()
    {
        if (Scanner()?.MaxRange is { } published and > 0) return published;

        var (mineGuns, _) = MiningWeapons();
        float mining = mineGuns.Count > 0 ? EffectiveRange(mineGuns) : 0f;

        return Math.Max(Math.Max(ScanReachFallback, mining), _scanProvenRange * 1.25f);
    }

    /// <summary>
    /// The scanner, but only when it is a scanner that can actually be cast.
    ///
    /// Knowing a scanner exists is not the same as being able to use one. <see cref="ScanSweepAsync"/>
    /// refuses to cast a scanner whose reach nothing has published — rightly, because the server
    /// silently drops out-of-range scan targets — and that refusal is permanent until a range is
    /// typed in. Meanwhile the mining loop was holding fire "waiting for the scan", counting
    /// unanswered casts to decide when to give up, on a scanner that never cast at all. The
    /// counter therefore never moved, the guns were never released, and the ship sat at full power
    /// beside a perfectly good rock indefinitely. That is the deadlock in the screenshot.
    ///
    /// Anything that stops the scan going out must therefore stop the wait as well.
    /// </summary>
    private Weapon? UsableScanner() => ScanReach() > 0 ? Scanner() : null;

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
        //
        // The raw list, not <see cref="Filtering"/>. Ticking all three resources no longer counts
        // as narrowing — correctly, because the guns must not be held for it — but the scan is
        // still read: it carries the ore COUNT, which is what ranks one confirmed rock above
        // another in RockValue. Gating the sweep on the narrowing test would have thrown that away.
        if (ScanOnlyWhenFiltering && WantedResources.Count == 0) return;

        // Already holding a queue of confirmed rocks? Then a scan buys nothing but a flat battery.
        // Scanning ran unconditionally before this, so a ship sitting on four known water rocks
        // still spent every spare point identifying a fifth instead of mining the first.
        //
        // But only rocks we can actually reach count towards that queue. Counting distant ones
        // let two confirmations on the far side of the belt switch scanning off entirely, so the
        // ship flew to them past hundreds of rocks it never looked at — and on arrival had
        // nothing local confirmed, so it flew somewhere else. A queue is only a queue if the
        // work in it is nearby.
        if (!WorthScanning(now)) return;

        // A scan the server refuses for want of power is indistinguishable from no scan at all —
        // and one that succeeds by draining the pool leaves the lasers unable to fire.
        if (!CanAffordScan(scanner)) return;

        // Weapon.MaxRange, not the raw slot stat. The stat stream is the only source the raw
        // lookup consults, and this server never sends it — so the scanner's reach fell back to
        // FallbackRange (3000u) and the range you typed into the loadout panel was ignored.
        //
        // Overstating the reach is not harmless: the batch then carries rocks the server
        // considers out of range, it refuses the cast outright, and a refusal is silent. That is
        // the "cast 3 times with no reply — most likely out of power cells" warning, which had
        // nothing to do with power cells.
        //
        // Which is exactly the argument RequireKnownReach makes for the guns, so the scanner is
        // held to it too. It used to fall through to FallbackRange regardless — aiming on the
        // very guess the setting exists to forbid, and manufacturing the silent refusals that
        // then read as a flat battery.
        float range = ScanReach();

        if (scanner.MaxRange is not > 0)
            WarnOnce($"scanner #{scanner.AbilityId} has no published reach — scanning within "
                   + $"{range:F0}u, which is measured (your mining reach, and the furthest a scan "
                   + "has actually been answered from) rather than the old 3,000u guess. Type its "
                   + "real range into the loadout panel to do better.");

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
            // No per-rock strikes here: one ghost in the batch makes the server refuse the whole
            // cast, so silence would frame every innocent rock that shared it. The hold watchdog
            // covers the area case; only the sector-wide counter learns anything from this.
            NoteScanSent(batch);
            return;
        }

        // The rock we're working comes first. Nearest is usually the same rock, but "usually" is
        // what left the guns held: a single-target scanner that keeps answering about something
        // else never unblocks the one target MineAsync is waiting on.
        //
        // But only if it is actually in reach. Preferring the target with no fallback meant that
        // while flying to an unscanned rock — the normal state, since the target is by definition
        // further away than the scanner can see — the sweep picked that rock, failed the range
        // test below, and returned having scanned NOTHING, with dozens of unscanned rocks sitting
        // well inside range the whole way. The ship surveyed nothing while it travelled, so every
        // rock had to be flown to before it could be identified.
        bool InReach(SpaceObj o) => (_world.DistanceToMe(o) ?? float.MaxValue) <= range;

        var rock = TargetNeedsScan(now) && _world.Get(CurrentTarget) is { } t && InReach(t) ? t : null;
        rock ??= _world.Nearest(o => NeedsScan(o, now) && ScanDue(o.Id, now) && InReach(o));
        if (rock is null) return;

        // Enough unanswered casts at THIS rock convicts the rock, not the scanner: a rock that
        // exists answers, a rock that doesn't swallows the cast without a word. Condemn it
        // instead of casting a third time — a scan reply is the only thing that could ever make
        // it mineable, and it has proven it won't give one.
        int strikes;
        lock (_gate) strikes = _scanStrikes.GetValueOrDefault(rock.Id);
        if (strikes >= ScanStrikesBeforeGone)
        {
            DropTarget(rock.Id, $"{strikes} scans answered by nothing — the rock is gone",
                       TimeSpan.FromMinutes(MuteRockSkipMinutes), hard: true);
            return;
        }

        await _act.CastSlotAbility(scanner.AbilityId, rock.Id);
        scanner.LastFired = now;
        ScansSent++;
        lock (_gate)
        {
            _scanAsked[rock.Id] = now;
            _scanStrikes[rock.Id] = strikes + 1;
        }
        NoteScanSent(rock.Id);
    }

    /// <summary>
    /// Counts casts that went out with no answer. A missing consumable is rejected in
    /// AbilityAction.preFun with no reply of any kind, so "out of power cells" is invisible on
    /// the wire — worth naming explicitly rather than letting it look like a broken scanner.
    ///
    /// The warning needs silence across more than one rock. A dead consumable mutes everything;
    /// one rock swallowing every cast is a rock that no longer exists, and the per-rock strikes
    /// deal with it without slandering the scanner.
    /// </summary>
    private void NoteScanSent(params uint[] rockIds)
    {
        lock (_gate)
            foreach (uint id in rockIds) _unansweredRocks.Add(id);

        if (++_scansWithoutReply < 3 || _ammoWarned) return;
        if (_unansweredRocks.Count < 2) return;
        _ammoWarned = true;
        Log?.Invoke("Scanner has been cast 3 times with no reply across different rocks. Most "
                  + "likely out of power cells (the Experimental module burns one per scan), "
                  + "otherwise out of power or out of range.");
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

    /// <summary>
    /// Whether a scan is worth its power right now.
    ///
    /// The scanner and the lasers draw on the same pool, so every scan is paid for in mining, and
    /// the two scanner shapes want opposite policies.
    ///
    /// An <b>area</b> scanner identifies a field per cast, so it is worth running until a queue
    /// has built up — <see cref="ScanQueueDepth"/> — and pointless after that.
    ///
    /// A <b>single-target</b> scanner identifies one rock per cast. A queue built one rock at a
    /// time costs mining time for information that only matters when the current rock runs out,
    /// so it scans exactly when there is nothing confirmed left to shoot, and stays quiet while
    /// there is. That is the whole loop: scan the nearest unknown, mine it if it is wanted, and
    /// only then look for the next.
    ///
    /// Either way the rock we are pointed at wins outright, because <see cref="MineAsync"/> holds
    /// fire until that one is identified — a guard that refuses to scan it is a guard that stops
    /// the ship dead.
    /// </summary>
    private bool WorthScanning(DateTime now)
    {
        if (TargetNeedsScan(now)) return true;

        return AreaScanner
            ? ConfirmedRocksNear(now) < ScanQueueDepth
            : !_world.Snapshot().Any(o => MiningCandidate(o) && KnownContents(o, now));
    }

    /// <summary>
    /// True when the rock we are currently pointed at still needs identifying.
    ///
    /// This is the one rock the scan gate must never starve, because it is the only rock anything
    /// is waiting on: <see cref="MineAsync"/> holds fire until its contents are known.
    /// </summary>
    private bool TargetNeedsScan(DateTime now)
    {
        uint id;
        lock (_gate) id = _target;
        if (id == 0) return false;

        var o = _world.Get(id);
        return o is not null && NeedsScan(o, now) && ScanDue(id, now);
    }

    /// <summary>
    /// True when our own position is an estimate that has had time to go wrong: the server has
    /// not stated where we are since the last time the ship was under way.
    ///
    /// Both halves matter. A fix older than <see cref="SelfPositionTrustSeconds"/> is not by
    /// itself a problem — a ship parked on a rock for two minutes has a two-minute-old fix and is
    /// exactly where that fix says, because nothing has moved it. Drift is something a flight
    /// does. So the test is "have we flown since the server last told us", not "is the fix old".
    /// </summary>
    private bool SelfPositionSuspect =>
        _world.MyFixAgeSeconds > SelfPositionTrustSeconds && _world.MyFixAt < _movedAt;

    /// <summary>
    /// Stops and waits for the server to say where we actually are, when we are about to commit
    /// to a decision that only makes sense if we are where we think.
    ///
    /// Stopping is not a delay tactic, it is the question: coming to rest is what makes the
    /// server broadcast a Rest maneuver, and a Rest states a position outright. It is also the
    /// same thing the caller was about to do anyway — this is the arrival, the throttle was
    /// coming off regardless. The only cost is the second or two before firing.
    ///
    /// Returns true if the caller should stand down this tick.
    /// </summary>
    private async Task<bool> ConfirmPositionAsync(DateTime now)
    {
        if (!SelfPositionSuspect)
        {
            _fixWaitSince = DateTime.MinValue;
            _fixWaitGaveUp = false;
            return false;
        }

        // Already asked and got nothing. Fly on the estimate rather than ask forever — see
        // SelfPositionWaitSeconds. Re-arms by itself as soon as any fix arrives.
        if (_fixWaitGaveUp) return false;

        if (_fixWaitSince == DateTime.MinValue)
        {
            _fixWaitSince = now;
            PositionResyncs++;
        }

        double waited = (now - _fixWaitSince).TotalSeconds;
        if (waited > SelfPositionWaitSeconds)
        {
            _fixWaitGaveUp = true;
            if (!_fixWaitWarned)
            {
                _fixWaitWarned = true;
                Log?.Invoke($"Stopped for {waited:F0}s and the server never sent a position for "
                          + "our own ship, so distances are being worked out from dead reckoning "
                          + "alone. Expect the odd rock to be given up on for being out of reach "
                          + "when it looked in range.");
            }
            return false;
        }

        await StopThrottleIfMoving();
        Status = $"Stopped to confirm where we are — the server's last fix is "
               + $"{_world.MyFixAgeSeconds:F0}s old and we have flown since";
        return true;
    }

    private async Task MineTick()
    {
        var (lasers, improvised) = MiningWeapons();
        var now = DateTime.UtcNow;
        await ScanSweepAsync();

        var rock = AreaScanner ? RankedTarget(now) : NearestTarget(now);

        // Nothing qualifies nearby? Then go and look, however far. Parking is the one outcome
        // that earns nothing at all.
        rock ??= Roam(now);

        if (rock is null)
        {
            Meter.Note(MiningActivity.Idle, now);
            await StopAllTogglesAsync();
            var all = _world.Snapshot();
            int rocks = all.Count(o => EntityTypes.IsMinable(o.Id));
            int located = all.Count(o => EntityTypes.IsMinable(o.Id) && o.HasPosition);
            string filter = !Filtering ? "" : $", filtering for {string.Join(" > ", WantedResources)}";
            Status = rocks == 0
                ? "No asteroids in the sector"
                : located == 0
                    ? $"{rocks} asteroid(s) known but none located — the server hasn't sent their WhoIs bodies"
                    : $"{located} asteroid(s) located, all skipped (depleted, on cooldown{filter})";

            // Going idle only ever set the status line, which is a transient header the moment
            // anything else happens — so a run that stalled overnight left no trace of why. It
            // belongs in the log, once per stall rather than once per tick.
            if (!_idle)
            {
                _idle = true;
                Log?.Invoke($"Farm stalled — {Status}");
            }
            return;
        }

        if (_idle)
        {
            _idle = false;
            Log?.Invoke($"Farming again — {rock} at {_world.DistanceToMe(rock) ?? 0f:F0}u.");
        }

        float dist = _world.DistanceToMe(rock) ?? float.MaxValue;
        float range = lasers.Count > 0 ? EffectiveRange(lasers) : FallbackRange;
        float preferred = StandoffFor(rock, lasers);

        // Say so when the keep-out is what sent us away.
        //
        // A rock inside an enemy station's envelope is silently disqualified by MiningCandidate,
        // and "silently" is the problem: with a 2,100u keep-out and an outpost in the middle of
        // the belt, every rock you can see out of the cockpit can be excluded at once, and the
        // bot sets off across the sector with nothing anywhere saying why. It looks broken. It
        // isn't — but you cannot tell that from the outside, which is just as bad.
        WarnAboutStationBubble(rock, dist, now);

        if (dist > range)
        {
            Meter.Note(MiningActivity.Travelling, now);
            if (AutoApproach)
            {
                await SteerToward(rock, preferred);
                Status = $"Closing on asteroid #{rock.Id:X8} — {dist:F0}u, hold at {preferred:F0}u"
                       + $" (r{rock.Radius:F0}), {SpeedInGear(_gear):F0}u/s {_gear}";
            }
            else
            {
                Status = $"Asteroid #{rock.Id:X8} is {dist:F0}u away, mining reach {range:F0}u (auto-approach off)";
            }
            return;
        }

        // We believe we have arrived. Everything from here — cutting the throttle, locking, firing
        // — is worthless if that belief came out of the integrator rather than off the wire, and
        // the failure is silent: the server refuses an out-of-range cast without saying so, so the
        // ship sits at the wrong place looking like it is mining until the stall watchdog gives up
        // on a rock that was never the problem. Prove it first.
        if (await ConfirmPositionAsync(now)) return;

        // A rock doesn't dodge, but accuracy still falls off past optimal range — close in.
        bool closing = AutoApproach && dist > preferred;
        if (closing) await SteerToward(rock, preferred);
        else await StopThrottleIfMoving();

        // Locking does NOT scan: the server's LockTarget handler only records the target id.
        // The scan is its own ability cast, handled by ScanSweepAsync above.
        await EnsureLocked(rock.Id);

        // Mining did not subscribe, so the rock's hull was never streamed and the one measurement
        // that says "these shots are landing" was missing. Combat has always done this; the cost
        // is one message per rock.
        await EnsureSubscribed(rock.Id);

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
        //
        // Unless the scanner is not answering. Waiting is only reasonable while an answer is
        // actually coming — a module that casts and never replies has run out of the consumable
        // it burns per scan, and holding for it parks the ship at full power next to a perfectly
        // good rock forever. An unenforceable filter is a reason to mine unfiltered and say so
        // loudly, not a reason to stop working.
        if (Filtering && UsableScanner() is not null && !KnownContents(rock, DateTime.UtcNow))
        {
            if (ScannerAnswering)
            {
                // The hold is a state like any other and gets a watchdog like any other. It
                // used to return here with nothing armed at all, so a rock that could never be
                // identified held the ship at full power indefinitely.
                if (WatchHeldFire(rock.Id, now,
                        "the scan that would identify it never came, so it is gone or "
                      + "cannot be identified")) return;

                Meter.Note(MiningActivity.Holding, now);
                await StopAllTogglesAsync();
                Status = $"Holding fire on #{rock.Id:X8} at {dist:F0}u — waiting for the scan"
                       + $" (power {_world.MyPower:F0}/{_world.MyMaxPower ?? 0f:F0})";
                return;
            }

            if (!_filterAbandoned)
            {
                _filterAbandoned = true;
                Log?.Invoke($"Scanner has not answered {_scansWithoutReply} casts — mining unfiltered "
                          + $"instead of waiting. Reload its consumable to get {string.Join(" > ", WantedResources)} "
                          + "filtering back.");
            }
        }

        var shooting = MiningFireSet(lasers, improvised);
        int fired = await FireAll(shooting, rock, dist, closing);

        // Is any laser actually in a position to shoot right now? Not "did one fire this tick" —
        // ticks outrun a reload — but "would the gates in FireAll let one through".
        //
        // This is the condition the stall watchdog has to be armed on, and getting it wrong is
        // expensive in both directions. It used to be `!closing`, which disarmed the watchdog for
        // any rock the ship was still creeping towards — so a rock that no longer existed was
        // farmed forever, showing "closing to 180u, holding (cooldown)" while nothing happened.
        // And a naive "arm it whenever we are in range" condemns innocent rocks whenever the guns
        // are held for want of a known reach or by HoldFireUntilOptimal.
        bool ableToFire = shooting.Any(w => CanEngage(w, dist, closing));
        if (!ableToFire)
        {
            // The same no-exit shape as the scan hold, and the same cure. Only while parked:
            // still closing means the state resolves by itself when the band is reached, and a
            // closing run that goes nowhere is the approach watchdog's case, not this one.
            if (!closing && WatchHeldFire(rock.Id, now,
                    "no reach known for any mining slot, or the hold position is outside "
                  + "every firing band")) return;

            Meter.Note(MiningActivity.Holding, now);
            Status = $"Holding fire on #{rock.Id:X8} at {dist:F0}u — "
                   + (shooting.Any(w => !RequireKnownReach || ReachKnown(w))
                       ? "no mining slot is inside its own firing band yet"
                       : "no reach known for any mining slot (no server stats, no card, nothing "
                       + "typed in) — fill it in on the loadout panel");
            return;
        }

        // Is anything actually coming off it? Casts leaving the ship are not evidence — the
        // server refuses a cast at an object that is gone, silently.
        //
        // The condition is "in position and working it", NOT "cast something this tick". Ticks
        // are faster than a half-second reload, so most of them legitimately fire nothing, and
        // watching `fired` would reset the clock every other tick and never reach the timeout.
        if (WatchMining(rock, ableToFire, now))
        {
            await StopAllTogglesAsync();
            return;
        }

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

    private DateTime _bubbleWarnedAt = DateTime.MinValue;

    /// <summary>
    /// Reports how much mining the hostile-station keep-out is costing, when it is costing any.
    ///
    /// Only fires when we are about to travel — if the chosen rock is close, the keep-out is not
    /// hurting and there is nothing to say. Rate-limited to once a minute, because the situation
    /// persists for as long as the ship is near the station and this must not become a flood.
    /// </summary>
    private void WarnAboutStationBubble(SpaceObj chosen, float dist, DateTime now)
    {
        if (!AvoidHostileStations) return;
        if (dist < LocalRadius) return;
        if ((now - _bubbleWarnedAt).TotalSeconds < 60) return;

        var stations = HostileStations();
        if (stations.Count == 0) return;

        int blocked = _world.Snapshot().Count(o => EntityTypes.IsMinable(o.Id)
                                                && o.MiningCooldown <= now
                                                && !IsCorpse(o)
                                                && InStationDanger(o));
        if (blocked == 0) return;

        _bubbleWarnedAt = now;
        var nearest = stations.OrderBy(s => _world.DistanceToMe(s) ?? float.MaxValue).First();
        // Naming the faction, because "WeaponPlatform" alone reads like the bot mistook your own
        // outpost for a gun. The type and the faction both come out of the object id itself
        // (SectorFactory / SpaceObject.ExtractFaction) — an Ancient or Cylon platform parked next
        // to a friendly outpost is a different object from the outpost, and the client calls it
        // an enemy by exactly the same rule this does.
        Log?.Invoke($"{blocked} asteroid(s) are inside the {HostileStationKeepOut:F0}u keep-out "
                  + $"around {nearest} ({EntityTypes.FactionOf(nearest.Id)}, "
                  + $"{_world.RelationTo(nearest.Id)}) — skipping them and travelling {dist:F0}u "
                  + "instead. Lower KEEP OFF GUNS, or turn off \"Avoid stations\", to mine them.");
    }

    /// <summary>True when the scanner identifies a whole field in one cast rather than one rock.
    /// Stated on the ability card, so this needs nothing measured or guessed.</summary>
    private bool AreaScanner => Scanner()?.Area == true;

    /// <summary>
    /// Nearest first, for the scanner this ship actually has.
    ///
    /// A single-target scanner identifies one rock per cast, so at any moment the bot knows the
    /// contents of one or two rocks out of four hundred. Ranking that by richness is ranking a
    /// sample of two: <see cref="RankedTarget"/> would commit two thousand units of travel to the
    /// best of them, which is not the best rock around, it is noise. And it cannot even see the
    /// alternative — an unscanned rock has a resource count of zero and can never win.
    ///
    /// So: mine the nearest rock we know holds what you asked for; failing that, go and identify
    /// the nearest rock we know nothing about. Travel is the only real cost of mining — the ore
    /// in a rock does not change how fast it comes out, only how often the trip is paid — so the
    /// rule that minimises travel is very close to the rule that maximises ore per hour.
    /// </summary>
    private SpaceObj? NearestTarget(DateTime now)
    {
        bool Any(SpaceObj o) => MiningCandidate(o);
        bool Wanted(SpaceObj o) => Any(o) && KnownContents(o, now);

        // Confirmed beats unconfirmed, then nearest wins within each. A rock we KNOW holds what
        // you asked for is worth more than one that might hold anything, and taking the nearest
        // of the confirmed set means this can never be the reason the ship travels far — if a
        // confirmed rock is close, it is the one chosen.
        //
        // Bounded to the scanner's own reach first: everything inside it is knowable from where
        // we stand, everything outside is a journey. Only when nothing within reach qualifies at
        // all does the unbounded pass below run.
        float local = LocalRadius;
        var localWanted = Bounded(Wanted, local);
        var localAny = Bounded(Any, local);

        // An unconfirmed held target is a guess, and a guess loses to knowledge: while anything
        // confirmed-and-wanted sits within local reach, keeping the guess is what parked the
        // ship on an unidentifiable rock with a rock KNOWN to hold what you asked for waiting in
        // the queue. A confirmed held target is still kept unconditionally — a worked rock is
        // finished, not churned. No oscillation is possible: the confirmed pass runs first and
        // its own pick passes this test, so the swap happens once and then holds.
        bool confirmedNearby = _world.Nearest(localWanted) is not null;
        bool Keep(SpaceObj o) => Any(o) && (KnownContents(o, now) || !confirmedNearby);

        if (confirmedNearby || _world.Nearest(localAny) is not null)
            return ResolveTarget(localWanted, honourPin: true, keep: Keep)
                ?? ResolveTarget(localAny, honourPin: true, keep: Keep);

        // Nothing within reach, so the sector is the search area — this is the trip that has to
        // be made before anything else can happen.
        //
        // `keep` stays LOOSER than each pass's own candidate, never stricter. Two passes over a
        // shared `_target` where the first pass's test is stricter than `keep` is what caused
        // the earlier churn: the strict pass rejected the held rock and cleared `_target`, the
        // looser pass immediately re-picked it, every tick — four "Engaging" log lines a second
        // and a LockTarget re-sent to the server at the same rate. The one thing Keep adds over
        // the raw candidate test is the confirmed-nearby eviction, and that is one-shot by
        // construction, not a churn.
        return ResolveTarget(Wanted, honourPin: true, keep: Keep)
            ?? ResolveTarget(Any, honourPin: true, keep: Keep);
    }

    /// <summary>
    /// Richest-per-distance among everything currently confirmed — the right rule once a single
    /// cast identifies a field at a time, and the wrong one before that.
    ///
    /// Kept whole for the area scanner it was written for. With thirty rocks confirmed at once
    /// there is a real population to rank, the resource priority list has something to choose
    /// between, and none of the sampling objections above apply.
    /// </summary>
    private SpaceObj? RankedTarget(DateTime now)
    {
        // With several resources picked, the highest-ranked one that has anything confirmed and
        // reachable wins outright — that is what makes the list a priority order rather than a
        // set. `keep` is deliberately the looser test: priority decides where we go NEXT, and a
        // rock already being worked is finished instead of being dropped the moment something
        // better respawns, which would throw away every shot already put into it.
        var tier = ConfirmedTier(now);
        if (tier is null) return ResolveTarget(MiningCandidate, honourPin: true);

        // Bounded to the local radius whenever anything qualifies inside it. Richest-per-distance
        // alone still let one very rich rock several thousand units away outrank a whole field
        // underneath the ship, and the trip costs minutes of not mining. Work out what is here
        // first; travel is what you do when here is exhausted.
        var local = Bounded(tier, LocalRadius);
        bool anyLocal = _world.Snapshot().Any(local);

        return ResolveTarget(anyLocal ? local : tier, RockValue, honourPin: true,
                             keep: o => MiningCandidate(o) && KnownContents(o, DateTime.UtcNow));
    }

    /// <summary>
    /// How worthwhile a scanned rock is: its resource count, discounted by the trip.
    ///
    /// The discount is now the SQUARE of the distance, which is the whole point. It used to be
    /// linear over 1000u, so a rock 5,000u away was only ~6x worse and any rock with six times
    /// the ore dragged the ship across the sector — for one trip that costs more time than the
    /// extra ore is worth, and the ship spends the run travelling instead of mining.
    ///
    /// Squared, the ore a far rock needs to win grows as the square of how far it is. At the
    /// default 1000u penalty a rock at 2,000u needs five times the ore — so 3,000 beats a nearby
    /// 500 and the ship makes the trip — while one at 5,000u needs twenty-six times, which it
    /// will not have. Worth a detour, not worth crossing the sector.
    /// </summary>
    private float RockValue(SpaceObj o, float distance)
    {
        float t = distance / Math.Max(RockTravelPenalty, 1f);
        return o.ResourceCount / (1f + t * t);
    }

    /// <summary>
    /// Where to go when nothing passes the normal filter.
    ///
    /// Almost everything <see cref="MiningCandidate"/> rejects is a BELIEF rather than a fact. A
    /// scan saying "empty" or "wrong resource" is up to <see cref="ScanFreshnessSeconds"/> old and
    /// the server refills rocks on its own timer. A skip is a note that an approach stalled
    /// minutes ago. Neither is a reason to sit still — so when the filter comes up empty those
    /// beliefs are dropped and the ship goes to the nearest rock it has no current knowledge of,
    /// at any distance, because arriving is what puts it back inside scanner range.
    ///
    /// A live mining cooldown and an enemy emplacement are the two exceptions: those are facts,
    /// and flying at either earns nothing or gets us shot.
    /// </summary>
    private SpaceObj? Roam(DateTime now)
    {
        // A hard skip is still honoured. Those are rocks we measured as unworkable — twenty
        // seconds of fire for no damage and no ore — and flying back to one is the loop the
        // mining watchdog exists to break.
        var unknown = _world.Nearest(o => EntityTypes.IsMinable(o.Id)
                                       && o.MiningCooldown <= now
                                       && !IsSkipped(o.Id, _hardSkip)
                                       && !IsCorpse(o)
                                       && !InStationDanger(o)
                                       && !KnownContents(o, now));
        if (unknown is null) return null;

        // The stall notes were what hid this rock from the normal path. Having decided to fly
        // there anyway, clear them, or it is rejected again the moment it has been scanned.
        int cleared;
        lock (_gate) { cleared = _skip.Count; _skip.Clear(); }

        // Adopted as the real target, not just flown at. Roaming used to bypass ResolveTarget
        // entirely, which left _target at 0 — so the scan gate could not see that the rock we
        // were on our way to still needed identifying, and DropTarget had nothing to clear if
        // it turned out to be a ghost.
        lock (_gate)
        {
            if (_target != unknown.Id) { _target = unknown.Id; _lockedTarget = 0; }
        }

        if (_roamTarget != unknown.Id)
        {
            _roamTarget = unknown.Id;
            Log?.Invoke($"Nothing worth mining in range — roaming to {unknown} at "
                      + $"{_world.DistanceToMe(unknown) ?? 0f:F0}u"
                      + (cleared > 0 ? $" ({cleared} skip(s) dropped)." : "."));
        }

        return unknown;
    }

    /// <summary>
    /// The best band of confirmed rocks currently available, as a predicate — or null if nothing
    /// is confirmed at all and we should go looking instead.
    ///
    /// With no filter that is simply "any confirmed rock". With a filter it walks the list in the
    /// order you picked, and stops at the first resource that actually has something worth flying
    /// to, so water never loses out to titanium just because the titanium happens to be nearer.
    /// </summary>
    private Func<SpaceObj, bool>? ConfirmedTier(DateTime now)
    {
        bool Confirmed(SpaceObj o) => MiningCandidate(o) && KnownContents(o, now);

        if (!Filtering)
            return _world.Nearest(Confirmed) is not null ? Confirmed : null;

        foreach (var want in WantedResources)
        {
            uint guid = (uint)want;
            bool Tier(SpaceObj o) => Confirmed(o) && o.ResourceGuid == guid;
            if (_world.Nearest(Tier) is not null) return Tier;
        }

        return null;
    }

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

    /// <summary>
    /// How far a rock can be and still count as "here". The scanner's own reach, because that is
    /// the radius the ship can survey without moving — everything inside it is knowable from
    /// where we stand, and everything outside is a journey.
    /// </summary>
    private float LocalRadius => ScanReach();

    /// <summary>The same test, but only for contacts within <paramref name="radius"/>.</summary>
    private Func<SpaceObj, bool> Bounded(Func<SpaceObj, bool> inner, float radius) =>
        o => inner(o) && (_world.DistanceToMe(o) ?? float.MaxValue) <= radius;

    /// <summary>Confirmed rocks close enough to work without crossing the sector.</summary>
    private int ConfirmedRocksNear(DateTime now)
    {
        float radius = LocalRadius;
        return _world.Snapshot().Count(o => MiningCandidate(o)
                                         && KnownContents(o, now)
                                         && (_world.DistanceToMe(o) ?? float.MaxValue) <= radius);
    }

    /// <summary>
    /// An object the server still lists but has already destroyed.
    ///
    /// <see cref="SpaceObj.Hull"/> defaults to 1 and only carries meaning once the server has
    /// streamed it, hence the <see cref="SpaceObj.StatsKnown"/> guard — otherwise every object
    /// we have never subscribed to would read as alive-by-default, which is the safe direction.
    /// </summary>
    private static bool IsCorpse(SpaceObj o) => o.StatsKnown && o.Hull <= 0f;

    private bool MiningCandidate(SpaceObj o)
    {
        if (!EntityTypes.IsMinable(o.Id)) return false;
        if (IsSkipped(o.Id)) return false;

        var now = DateTime.UtcNow;
        if (o.MiningCooldown > now) return false;

        // A corpse. The server does not always send a removal for a rock that has been broken
        // open — it just stops existing as far as the game is concerned while its object lingers
        // in our world model — but it DOES stream the hull, and a hull of zero is unambiguous.
        //
        // Nothing checked this, so the bot would happily pick a rock it had itself destroyed
        // minutes earlier and fly a thousand units to it, then sit there "closing" on nothing.
        // The mining watchdog could not save it either: that only arms once the ship is in
        // position and able to fire, and a ship still flying towards a ghost never gets there.
        //
        // Guarded on StatsKnown because Hull defaults to 1 and only means anything once the
        // server has actually streamed it — which, for mining, it now does, because the target
        // is subscribed.
        if (IsCorpse(o)) return false;

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
        if (Filtering && known && !WantsResource(o.ResourceGuid))
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
    /// Whether a weapon's reach is actually known, from any of the three real sources.
    ///
    /// A weapon with no known reach cannot be aimed, and guessing high is the expensive
    /// direction to be wrong in: the server refuses an over-range cast <em>silently</em>, so the
    /// shot is spent, the cooldown starts, and nothing says why. Guessing low only costs a
    /// slightly closer approach. So an unknown reach means hold fire, not invent 3,000u.
    /// </summary>
    private bool ReachKnown(Weapon w) => w.MaxRange is > 0;

    /// <summary>
    /// Whether this weapon is in a position to fire at <paramref name="distance"/>, ignoring
    /// cooldown and power — i.e. everything about the geometry and nothing about the clock.
    ///
    /// Single source of truth on purpose. <see cref="FireAll"/> decides whether to pull the
    /// trigger and <see cref="WatchMining"/> decides whether the absence of damage is the rock's
    /// fault; when those two disagreed, the watchdog condemned rocks the guns were never allowed
    /// to shoot at in the first place.
    /// </summary>
    private bool CanEngage(Weapon w, float distance, bool stillClosing)
    {
        if (RequireKnownReach && !ReachKnown(w)) return false;
        if (w.MaxRange is { } max && distance > max) return false;
        if (w.MinRange is { } min && distance < min) return false;
        if (HoldFireUntilOptimal && stillClosing
            && w.OptimalRange is { } opt && opt > 0 && distance > opt) return false;
        return true;
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
            //
            // Floored by the same clearance the collision code uses, exactly as the planetoid
            // case below is. Without it the approach aimed for a flat 180u while the avoidance
            // considered anything inside 183u a collision, so the two pulled against each other
            // for as long as the bot was pointed at the rock: closing, shoved out, closing again,
            // never in position, never firing. A standoff the avoidance will not permit is not a
            // standoff.
            case SpaceEntityType.Asteroid when AsteroidStandoff > 0:
            {
                // A gap to the SURFACE, plus the rock's own radius — not a distance from its
                // centre. Centre-to-centre made the setting meaningless: it was floored by
                // radius + margin, so on any rock bigger than the number you typed the floor won
                // and typing 50 changed nothing you could see.
                float gap = target.Radius + AsteroidStandoff;

                // And never exactly ON the clearance sphere, which is what the old
                // Max(standoff, ClearanceOf) produced whenever the floor won. Park on the
                // boundary and the rock counts as "in the way" the instant it stops being the
                // target — one drift, one rotation, one re-target and the ship is suddenly
                // escaping the thing it was mining a moment ago. That is the churn in the log:
                // engage at 168u, then "0u of room left ahead" against the very same rock.
                float floor = ClearanceOf(target) * StandoffMargin;

                // Reach still caps it, but never below the floor: a firing position we would
                // treat as a collision is not a firing position.
                return Math.Clamp(Math.Max(gap, floor), floor, Math.Max(reach * 0.95f, floor));
            }

            // Not clamped to weapon reach: a planetoid is worked by ordering a mining ship, not by
            // shooting it, so there is nothing to stay in range of. It IS floored by the body's
            // own size, because the configured number is a flat 1200u and planetoids are not all
            // smaller than that — holding at 1200u from the centre of something with a 2000u
            // radius is not a standoff, it is a stated intention to fly into it.
            case SpaceEntityType.Planetoid when PlanetoidStandoff > 0:
                // The same clearance the collision code uses, so the approach cannot ask for a
                // hold position the avoidance considers a collision — which is a standoff and a
                // dodge pulling against each other for as long as the bot is pointed at it.
                return target.Radius > 0
                    ? Math.Max(PlanetoidStandoff, ClearanceOf(target))
                    : PlanetoidStandoff;
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
        int castsThisTick = 0;

        foreach (var w in guns)
        {
            if (!CastAllowed(w.AbilityId, target.Id)) continue;

            if (RequireKnownReach && !ReachKnown(w))
                WarnOnce($"ability #{w.AbilityId} has no known reach from stats, the catalogue "
                       + "or your loadout — holding fire rather than guessing.");

            if (!CanEngage(w, distance, stillClosing)) continue;
            if (!CanAfford(w)) continue;

            // Rate cap, and the reason it exists: this used to fire every gun in the same
            // millisecond. A nine-cast burst inside 3ms is not something a person at a keyboard
            // can produce, and the server closed the connection immediately after every one of
            // them. Spreading them over consecutive ticks costs a fraction of a second of
            // damage and stops the bot looking like a packet flood.
            if (w.Kind != WeaponKind.Toggle && castsThisTick >= MaxCastsPerTick) continue;

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
            castsThisTick++;

            // Booked before the server answers, because the answer carries no range: a hit
            // report says what we did, never where we were standing when we did it.
            //
            // Rocks excluded. The curve being built is hit rate against a target's avoidance,
            // and an asteroid has none — it cannot be missed. Worse, a mining hit may not come
            // back as CombatInfo at all, so every laser shot would age out as a miss and, since
            // mining fires far more shots than combat ever does, the whole table would end up
            // describing the lasers rather than the guns.
            if (!EntityTypes.IsMinable(target.Id))
                Fights.NoteShotFired(w.AbilityId, target.Id, distance, ThrottleFraction);
        }

        return firing;
    }

    /// <summary>
    /// Whether an ability id is safe to cast at all.
    ///
    /// <c>PlayerProtocol Reply.Slots</c> is the server stating which slots this ship has. An
    /// ability id absent from that list does not exist to fire — and the bot can easily hold
    /// one, because ids are remembered in <c>bot.json</c> across refits and across ships. A
    /// remembered id belonging to a hull you no longer fly is a cast the server has every right
    /// to treat as nonsense.
    ///
    /// Silent when the slot list has not arrived: on a server that never sends it, refusing
    /// everything would mean never firing. The check only bites when there is something
    /// authoritative to check against.
    /// </summary>
    private bool CastAllowed(ushort abilityId, uint targetId)
    {
        if (!ClientCanSee(targetId))
        {
            WarnOnce($"#{targetId:X8} is outside your detection range — not firing at it.");
            return false;
        }

        var slots = _world.MySlots();
        if (slots.Count == 0) return true;

        // A slot list where NOTHING is fitted is not describing the ship we are flying — we are
        // clearly reading the wrong hangar entry, or the server declined to detail it. Trusting
        // it refuses every weapon on the ship, which is exactly what happened: the mining lasers
        // were blocked as "empty" while the scanner and the repair module sailed through because
        // they happened to be listed. A list that describes nothing gets to veto nothing.
        if (!slots.Any(s => s.Filled))
        {
            WarnOnce("the slot list the server sent has no fitted slots in it — ignoring it "
                   + "rather than letting it veto every weapon.");
            return true;
        }

        var slot = slots.FirstOrDefault(s => s.SlotId == abilityId);
        if (slot is null)
        {
            WarnOnce($"ability #{abilityId} is not a slot on this ship — not casting it. "
                   + "It is probably remembered from another ship; clear it in the loadout panel.");
            return false;
        }

        // An empty or broken slot answers a cast with nothing at best.
        if (!slot.Filled)
        {
            WarnOnce($"slot #{abilityId} is empty — not casting it.");
            return false;
        }
        if (slot.Inoperable)
        {
            WarnOnce($"slot #{abilityId} is broken — not casting it.");
            return false;
        }

        // The catalogue states what a slot's ability actually DOES. Without it the bot has been
        // treating roughly every slot as a gun and pulling the trigger on engines and computers,
        // which is not a weapon misfiring — it is asking the server to do something meaningless.
        if (ActionOf(slot.SystemGuid) is { } action && !IsOffensive(action))
        {
            WarnOnce($"slot #{abilityId} is {action}, not a weapon — not casting it at things.");
            return false;
        }

        return true;
    }

    /// <summary>What a fitted system's ability does, if the catalogue has reached us. Null means
    /// unknown, and unknown is allowed through — the cache fills in over time and refusing
    /// everything until it is complete would stop the bot firing at all.</summary>
    private AbilityActionType? ActionOf(uint systemGuid)
    {
        if (systemGuid == 0) return null;
        var system = Cards.System(systemGuid);
        if (system is null) return null;

        foreach (var guid in system.AbilityCardGuids)
            if (Cards.Ability(guid) is { } ability) return ability.EffectiveAction;

        return null;
    }

    /// <summary>Abilities that are meant to be pointed at an enemy. Everything else — buffs,
    /// repairs, flares, stealth, scanners — is either self-targeted or has its own trigger.</summary>
    private static bool IsOffensive(AbilityActionType a) => a is
        AbilityActionType.FireCannon or AbilityActionType.FireMissle or
        AbilityActionType.FireMining or AbilityActionType.FireTorpedo or
        AbilityActionType.FireLightMissile or AbilityActionType.FireHeavyMissile or
        AbilityActionType.FireShotgun or AbilityActionType.FireKillCannon or
        AbilityActionType.FireMachineGun or AbilityActionType.Flak or
        AbilityActionType.PointDefence or AbilityActionType.Debuff or
        AbilityActionType.ShortCircuit or AbilityActionType.ActivatePaintTheTarget;

    private bool _loadoutDumped;

    /// <summary>
    /// Prints the slot list the server actually sent, the first time one arrives.
    ///
    /// Every question about this subsystem — is the message arriving, is it keyed to the ship we
    /// are flying, do the slot ids line up with the positions in your ability bar — has been
    /// unanswerable because nothing ever showed what was received. One line per slot ends that.
    /// </summary>
    private void DumpLoadoutOnce()
    {
        if (_loadoutDumped) return;
        _loadoutDumped = true;

        var hangar = _world.HangarSummary();
        Log?.Invoke($"Slot list arrived. Active ship id {_world.MyShipId}; hangar holds "
                  + string.Join(", ", hangar.Select(h => $"#{h.ShipId} ({h.Filled}/{h.Slots} filled)")));

        var slots = _world.MySlots();
        if (slots.Count == 0) { Log?.Invoke("  ...but the chosen entry has no slots at all."); return; }

        foreach (var s in slots)
        {
            var action = ActionOf(s.SystemGuid);
            Log?.Invoke($"  slot {s.SlotId,-3} {(s.Filled ? $"system {s.SystemGuid}" : "EMPTY")}"
                      + (s.Inoperable ? " BROKEN" : "")
                      + (action is { } a ? $"  → {a}" : s.Filled ? "  → not in the card cache" : ""));
        }
    }

    private readonly HashSet<string> _warned = [];

    /// <summary>Logs a message the first time only. A per-tick refusal would otherwise repeat
    /// four times a second for as long as the fight lasts.</summary>
    private void WarnOnce(string message)
    {
        lock (_gate) { if (!_warned.Add(message)) return; }
        Log?.Invoke(message);
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

    /// <param name="keep">Test applied to the target we already have, when it should be looser
    /// than the test used to pick a new one — a priority filter that would re-pick every tick
    /// otherwise drops a half-mined rock the instant a better one appears. Defaults to
    /// <paramref name="candidate"/>, which is the same behaviour as before.</param>
    private SpaceObj? ResolveTarget(Func<SpaceObj, bool> candidate, Func<SpaceObj, float, float>? score = null,
                                    bool honourPin = false, Func<SpaceObj, bool>? keep = null)
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

                // A pin we have since given up on does not get to win. It used to: the only
                // release was the object leaving the world, and a rock the server never sent a
                // removal for never leaves — so a pinned ghost was re-selected every tick and
                // outlived both a manual reselection and a stop/start of the farm.
                if (IsSkipped(pin))
                {
                    lock (_gate) { _pinned = 0; if (_target == pin) { _target = 0; _lockedTarget = 0; } }
                    Log?.Invoke("Pinned target was given up on — picking targets automatically again.");
                }
                else if (held is not null && held.HasPosition && shaped)
                {
                    lock (_gate) _target = pin;
                    return held;
                }
                else if (held is null)
                {
                    lock (_gate) { _pinned = 0; if (_target == pin) { _target = 0; _lockedTarget = 0; } }
                    Log?.Invoke("Pinned target is gone — picking targets automatically again.");
                }
            }
        }

        if (current != 0)
        {
            var held = _world.Get(current);
            if (held is not null && (keep ?? candidate)(held) && held.HasPosition) return held;
            lock (_gate) { if (_target == current) { _target = 0; _lockedTarget = 0; } }
        }

        // No throttle here on purpose: the search only runs when there is no valid target,
        // and throttling it made the status line alternate between "engaging" and
        // "no hostiles" every other tick.
        _lastRetarget = DateTime.UtcNow;

        var next = score is null ? _world.Nearest(candidate) : _world.Best(candidate, score);
        if (next is null) return null;

        lock (_gate) { _target = next.Id; }

        // Only when it is genuinely a different target. Re-selecting the same object is not news,
        // and a caller that re-picks every tick would otherwise fill the log at four lines a
        // second — which is how the churn above stayed invisible for so long.
        if (next.Id != current)
            Log?.Invoke($"Engaging {next} at {_world.DistanceToMe(next):F0}u");
        return next;
    }

    /// <summary>
    /// Refuse to act on a contact your own client cannot see.
    ///
    /// The bot knows about objects the client draws nothing for — WhoIs tells us about things
    /// well past every detection radius, which is why the panel can report contacts as
    /// "client-dark". Locking, subscribing to or firing at one of those is an action no real
    /// client could ever produce: the player has no way to select something that is not on the
    /// DRADIS. Asking the server to do it anyway is asking it to believe we can see through the
    /// fog, and it has no reason to.
    ///
    /// Permissive when the radii have not been published — refusing everything on a server that
    /// never sends detection stats would stop the bot working entirely.
    /// </summary>
    private bool ClientCanSee(uint id)
    {
        if (!HuntOnlyVisible) return true;

        var det = _world.Detection;
        if (!det.Known) return true;

        var o = _world.Get(id);
        if (o is null || !o.HasPosition) return true;

        return _world.LayerOf(o, det) != ContactLayer.Dark;
    }

    private async Task EnsureLocked(uint id)
    {
        if (_lockedTarget == id) return;
        if (!ClientCanSee(id))
        {
            WarnOnce($"#{id:X8} is outside your detection range — not locking it.");
            return;
        }
        await _act.LockTarget(id);
        _lockedTarget = id;
    }

    /// <summary>
    /// Ask the server to stream the target's hull and power.
    ///
    /// Was briefly disabled on the theory that it caused the combat disconnects, being the one
    /// message combat sends and mining does not. A session that sent none of them and dropped
    /// anyway settled that: it is innocent, so it is back on. It supplies the target's hull
    /// readout and <c>TargetId</c>, which is how the bot knows something has locked us.
    /// </summary>
    public bool SubscribeToTarget { get; set; } = true;

    private async Task EnsureSubscribed(uint id)
    {
        if (!SubscribeToTarget) return;
        if (_subscribedTarget == id) return;
        if (_subscribedTarget != 0)
        {
            try { await _act.UnSubscribeInfo(_subscribedTarget); } catch { }
        }
        await _act.SubscribeInfo(id);
        _subscribedTarget = id;
    }

    private void Skip(uint id, TimeSpan how) { lock (_gate) _skip[id] = DateTime.UtcNow + how; }

    /// <summary>
    /// A skip <see cref="Roam"/> is not allowed to forget.
    ///
    /// The ordinary skip list is a set of beliefs that go stale — "this approach stalled", "this
    /// looked depleted" — and roaming drops all of them on purpose, because sitting still is
    /// worse than re-checking something. But a rock the mining watchdog gave up on is not a
    /// stale belief, it is a measurement: twenty seconds of firing produced no damage and no
    /// ore. Putting it in the same list meant roaming wiped it and immediately picked the same
    /// ghost rock again, since roaming deliberately ignores skips — watchdog fires, roam
    /// forgets, watchdog fires, forever.
    /// </summary>
    private void SkipHard(uint id, TimeSpan how) { lock (_gate) _hardSkip[id] = DateTime.UtcNow + how; }

    /// <summary>
    /// Give up on a target, for a reason, and make the decision stick.
    ///
    /// Clearing <see cref="_target"/> alone was never enough. A pin outranks every targeting rule
    /// in <see cref="ResolveTarget"/> and was only ever released when the object left the world —
    /// so a pinned rock that had ceased to exist, but which the server never sent a removal for,
    /// was re-selected on the very next tick, forever, and selecting something else by hand did
    /// not survive one tick either. Deciding a target is no good has to release the pin too.
    /// </summary>
    /// <param name="hard">Whether the skip survives roaming. True when we MEASURED that the
    /// target is no good, rather than merely believing it.</param>
    private void DropTarget(uint id, string why, TimeSpan? skipFor = null, bool hard = false)
    {
        bool unpinned;
        lock (_gate)
        {
            unpinned = _pinned == id;
            if (unpinned) _pinned = 0;
            if (_target == id) { _target = 0; _lockedTarget = 0; }
        }

        if (skipFor is { } how) { if (hard) SkipHard(id, how); else Skip(id, how); }
        _approachId = 0;
        _mineWatchId = 0;
        _holdId = 0;

        Log?.Invoke($"Dropped #{id:X8} — {why}."
                  + (unpinned ? " Pin released; picking targets automatically again." : ""));
    }

    /// <summary>
    /// Returns true when the rock we are shooting should be abandoned.
    ///
    /// The approach watchdog cannot cover this: it fires on a straight-line distance that stops
    /// changing, and a ship holding station at its standoff has exactly that by design. Worse, it
    /// is only ever called from <see cref="SteerToward"/>, which a ship already in position does
    /// not call — so from the moment the bot arrived at a rock, nothing checked the target again.
    ///
    /// Progress is deliberately measured on results rather than on effort. Casts sent prove
    /// nothing: the server refuses a cast at an object that no longer exists in
    /// <c>AbilityAction.preFun</c> and says nothing back, so a ghost rock reads as a full burst
    /// leaving the ship every tick. Hull coming off, or ore reaching the hold, cannot be faked.
    /// </summary>
    /// <param name="working">In position with mining weapons on this rock. Not "fired this
    /// tick" — ticks outrun a half-second reload, so that would reset the clock forever.</param>
    private bool WatchMining(SpaceObj rock, bool working, DateTime now)
    {
        if (!working) { _mineWatchId = 0; return false; }

        float hull = rock.Hull;
        uint left = rock.ResourceCount;
        long banked = Meter.MinedGained;

        if (_mineWatchId != rock.Id)
        {
            _mineWatchId = rock.Id;
            _mineProgressAt = now;
            _mineHull = hull;
            _mineOreLeft = left;
            _mineOreBanked = banked;
            return false;
        }

        if (hull < _mineHull - 0.01f || left < _mineOreLeft || banked > _mineOreBanked)
        {
            _mineProgressAt = now;
            _mineHull = hull;
            _mineOreLeft = left;
            _mineOreBanked = banked;
            return false;
        }

        if ((now - _mineProgressAt).TotalSeconds < MiningStallSeconds) return false;

        DropTarget(rock.Id, $"{MiningStallSeconds:F0}s of firing with no damage dealt and no ore "
                          + "banked — it is gone, or we cannot reach it",
                   TimeSpan.FromMinutes(5), hard: true);
        return true;
    }

    /// <summary>
    /// Returns true when the ship has been engaged on one rock, with no shot possible, for long
    /// enough that the engagement itself is the problem. The stall watchdog cannot cover this:
    /// it deliberately disarms while the guns are not firing, so every hold state — waiting for
    /// a scan, no known reach, parked outside the firing bands — was a state with no exit.
    ///
    /// Self-arming and self-forgetting: the clock runs only across CONSECUTIVE held ticks, so an
    /// interruption — combat, travel, a different target — starts a fresh count instead of
    /// inheriting a stale one and condemning a rock the moment the hold resumes. The clock is
    /// shared across the hold reasons on purpose: a rock that alternates between "waiting for
    /// the scan" and "outside the band" has still produced nothing the whole time.
    /// </summary>
    private bool WatchHeldFire(uint rockId, DateTime now, string why)
    {
        if (_holdId != rockId || (now - _holdSeen).TotalSeconds > 2)
        {
            _holdId = rockId;
            _holdSince = now;
        }
        _holdSeen = now;

        if ((now - _holdSince).TotalSeconds < HeldFirePatienceSeconds) return false;

        DropTarget(rockId, $"engaged {HeldFirePatienceSeconds:F0}s without a single shot possible "
                         + $"— {why}",
                   TimeSpan.FromMinutes(MuteRockSkipMinutes), hard: true);
        return true;
    }

    private bool IsSkipped(uint id) => IsSkipped(id, _skip) || IsSkipped(id, _hardSkip);

    private bool IsSkipped(uint id, Dictionary<uint, DateTime> list)
    {
        lock (_gate)
        {
            if (!list.TryGetValue(id, out var until)) return false;
            if (until > DateTime.UtcNow) return true;
            list.Remove(id);
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
    /// How open the throttle is, 0..1 — the quantity the avoidance rule is thought to key on.
    ///
    /// Boost reports 1 outright rather than a ratio: the boost gear is a separate state, not a
    /// throttle position, and the stored throttle it will fall back to says nothing about how
    /// hard the ship is currently moving.
    ///
    /// Zero while the throttle has never been opened, which is honest — a ship that has not been
    /// commanded anywhere is stationary.
    /// </summary>
    public float ThrottleFraction
    {
        get
        {
            if (_gear == Gear.Boost) return 1f;
            if (!_throttleOpen) return 0f;
            float top = TopSpeed;
            return top > 0f ? Math.Clamp(_throttle / top, 0f, 1f) : 0f;
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
    /// How far out a target has to be before boosting to it is worth doing — the distance the
    /// ship needs to arrive at the hold position under control.
    ///
    /// Both terms are measured at BOOST speed, deliberately. This is the decision "is there room
    /// to run fast and still stop", so it has to be answered against the fast case, not against
    /// the cruise the ship happens to be in while asking.
    /// </summary>
    private float BoostRunway(float stopRange)
    {
        float brake = Math.Clamp(BoostSpeed * BrakingSeconds, MinBrakeDistance, BrakingDistance);
        return stopRange + brake + BoostSpeed * BoostShedSeconds;
    }

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
        var desired = target.PredictedPosition(now) - _world.MyPosition;

        // Gear is decided first because everything below is measured in seconds of travel, and
        // how far a second is depends on which gear we're in. It needs nothing but the distance,
        // so there is no circularity — and if an obstacle forces Regular later, the zones stay
        // sized for boost, which errs towards more room rather than less.
        var gear = UseBoost && BoostSpeed > 0f && distance > BoostRunway(stopRange)
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

        // After the deflection, not before: the watchdog has to know whether we are stalled or
        // merely going the long way round, and those look identical from the distance alone.
        if (watchdog && WatchApproach(target.Id, distance, now, deflected)) return;

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
            // Getting OUT of something outranks arriving at something. The old line took the
            // minimum of the two, so a ship inside a big rock's clearance sphere — gap 0, taper
            // 0, throttle pinned to MinApproachSpeed — crawled its way out over tens of seconds
            // while the mining brake held it down as well. That is the wedge: the one state where
            // the ship is definitely in the wrong place is the state it left at walking pace.
            throttle = blocker.Id == _escapeFrom
                ? EscapeThrottle(blocker, now)
                : Math.Min(throttle, ThrottlePastObstacle(blocker, gap, brakeZone));

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
            float clear = ClearanceOf(o);

            // Hysteresis on the one we are getting out of: it counts as "inside" until we are
            // clear of it by a margin. Leaving on the exact boundary is what let the ship exit a
            // big rock's sphere and be aimed straight back into it on the very next tick.
            if (o.Id == _escapeFrom) clear *= EscapeClearance;

            // Already inside it. Tested before anything about our heading, because once the ship
            // is within a big body's clearance sphere the direction it is pointing stops being
            // the question — every direction except outwards is wrong.
            //
            // Nothing checked this before, and the "is it in front of us" test below quietly
            // hid it: the moment a planetoid's centre went abeam, `along` turned negative and the
            // body stopped counting as an obstacle at all. On an asteroid that blind window is a
            // second; on a body a thousand units across it is most of a minute, during which the
            // throttle went back to full towards whatever was on the far side. That is the ram.
            float centre = toObs.Length();
            if (centre < clear)
            {
                // Deepest first if we have somehow got inside two, but never displaced by an
                // ordinary obstacle further down the list.
                if (nearestAlong == float.MinValue && nearest is not null
                    && centre >= Vector3.Distance(nearest.PredictedPosition(now), me)) continue;

                nearest = o;
                nearestAlong = float.MinValue;   // nothing outranks something we are already in
                gap = 0f;
                continue;
            }

            // How far along our heading it sits. Negative means it is behind us, and flying away
            // from something is never the problem.
            float along = Vector3.Dot(toObs, dir);
            if (along <= 0f) continue;

            // Big things have to be seen from further out, because getting around one means
            // moving its whole width sideways, and the ship turns at a fixed rate however large
            // the obstacle is. A flat time-based lookahead is fine for a rock and nowhere near
            // enough for a planetoid: ~430u of warning against a body 2000u across is a turn that
            // cannot be completed, so the ship grinds along the surface instead of clearing it.
            float react = Math.Max(lookahead, clear);
            if (along - clear > react) continue;

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
        if (blocker is null)
        {
            _escapeFrom = 0;
            return desired;
        }

        var me = _world.MyPosition;
        var dir = Vector3.Normalize(desired);
        var obs = blocker.PredictedPosition(now);
        var toObs = obs - me;

        float along = Vector3.Dot(toObs, dir);
        float clear = ClearanceOf(blocker);
        if (blocker.Id == _escapeFrom) clear *= EscapeClearance;

        // Inside it: there is no "around" to steer, only "out". Straight away from the centre is
        // the shortest way back to open space, and it is the one heading that is guaranteed to
        // reduce the overlap no matter where the target is.
        float centre = toObs.Length();
        if (centre < clear)
        {
            if (_escapeFrom != blocker.Id) { _escapeFrom = blocker.Id; _escapeSince = now; }
            deflected = true;
            return centre > 1f ? -toObs / centre * clear : SidestepAxis(dir) * clear;
        }

        // Out, with the margin to prove it.
        if (_escapeFrom == blocker.Id) _escapeFrom = 0;

        // Push the aim to whichever side our path already favours: that is the smaller course
        // change, and it keeps the deflection stable instead of flip-flopping between the two
        // ways round on consecutive ticks.
        var offset = dir * along - toObs;
        float lateral = offset.Length();
        var side = lateral > 1f ? offset / lateral : SidestepAxis(dir);

        // `+ dir * clear` is what turns a dodge into a way past.
        //
        // Without it the aim is a fixed point abeam the obstacle, at a set distance from its
        // centre — so the ship flies to that point and then has nowhere further to go. The direct
        // line is still blocked, so the deflection re-arms, and the aim vector shrinks to a few
        // units whose direction swings wildly tick to tick. That is the loop: the ship hangs off
        // the side of a planetoid jittering, never clearing it, forever. Leading the aim past the
        // obstacle along the original heading gives the path somewhere to go.
        var aim = obs + side * (clear * 1.25f) + dir * clear - me;

        // A degenerate aim used to fall back to `desired`, which points into the thing we are
        // dodging. The tangent is always the safer answer.
        if (aim.LengthSquared() < 1f) aim = side * clear;

        // One second opinion, because in a belt the shortest way round one rock is very often
        // straight into the next one. The ship then brakes for THAT one, deflects back, and
        // oscillates between the pair at a crawl — which is what "0u of room left ahead" every
        // few seconds against two different asteroids actually was.
        //
        // The other way round is only taken when it is genuinely clear; swapping to a second
        // blocked path would just move the wedge, and a coin flip between two bad headings is
        // worse than committing to one.
        if (BlockerAhead(aim, lookahead, ignoreId, now, out _) is { } second && second.Id != blocker.Id)
        {
            var other = obs - side * (clear * 1.25f) + dir * clear - me;
            if (other.LengthSquared() >= 1f
                && BlockerAhead(other, lookahead, ignoreId, now, out _) is null)
                aim = other;
        }

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

    /// <summary>
    /// Throttle to hold while something is in the way.
    ///
    /// The zone widens with the obstacle for the same reason the reaction distance does. A taper
    /// measured in seconds of travel is a fixed ~140u, which against a planetoid means full speed
    /// until 140u from its surface and then about a second and a half to stop. That is not a
    /// braking zone, it is an impact — and the ship bounces off, re-aims, and does it again.
    /// </summary>
    private float ThrottlePastObstacle(SpaceObj blocker, float gap, float brakeZone)
    {
        float zone = Math.Max(brakeZone, ClearanceOf(blocker));
        float t = Math.Clamp(gap / zone, 0f, 1f);
        return Math.Max(TopSpeed * MathF.Sqrt(t), MinApproachSpeed);
    }

    /// <summary>How far past an obstacle's clearance to get before we stop calling ourselves
    /// inside it. Pure hysteresis: it exists so leaving is a decision, not a boundary case.</summary>
    public float EscapeClearance { get; set; } = 1.25f;

    /// <summary>
    /// Throttle while backing out of something we are already inside.
    ///
    /// The ordered heading here points directly away from the obstacle, so speed is the cure and
    /// not the danger — the faster we go, the sooner we are somewhere the normal rules work
    /// again. The one exception is momentum still carrying us inwards: the ship has to turn
    /// before it can leave, and full throttle through that turn puts us deeper first.
    /// </summary>
    private float EscapeThrottle(SpaceObj blocker, DateTime now)
    {
        var away = _world.MyPosition - blocker.PredictedPosition(now);
        var vel = _world.MyVelocity;

        bool leaving = vel.LengthSquared() < 1f || Vector3.Dot(vel, away) > 0f;
        return leaving ? TopSpeed : Math.Max(TopSpeed * 0.5f, MinApproachSpeed);
    }

    /// <summary>
    /// How much room a solid object needs to be flown PAST, measured from its centre.
    ///
    /// Deliberately not <see cref="RadiusClearance"/>. That multiplier (×3) is a *standoff*
    /// figure — where to park when you have a choice — and it was briefly used here on the
    /// argument that the two halves of the program should agree about how big things are. They
    /// should not: parking beside a body and threading between bodies are different questions,
    /// and answering the second with the first is what broke mining.
    ///
    /// A 53u rock went from needing 183u of room to needing 289u. In a belt of 130 asteroids that
    /// means the ship is permanently inside somebody's exclusion sphere, so every tick reported
    /// "0u of room left ahead", clamped the throttle to a crawl and pushed the heading outward.
    /// It could not cross its own asteroid field. The rock it was aiming at then failed the
    /// approach watchdog, got skipped, and the same thing happened to the next one — until every
    /// rock nearby was skipped and the nearest survivor was eight thousand units away.
    ///
    /// The planetoid ram that started all this is fixed by the three changes that actually
    /// address it — the already-inside test, the size-scaled lookahead, and leading the aim past
    /// the obstacle — none of which need the radius inflated.
    /// </summary>
    /// <remarks>
    /// Split by what the body actually is, because one number cannot serve both ends of a range
    /// that spans two orders of magnitude. A flat +70u on an 18u pebble is a no-go sphere five
    /// times the rock's own size, and in a belt of those the ship is permanently inside somebody's
    /// — that is the "35u of room left ahead" churn. The same +70u on a 1,500u planetoid is 4% of
    /// its radius, which is no margin at all on something you arrive at doing 80u/s.
    ///
    /// So: asteroids get their real collider and a small ship-sized margin; planetoids get a
    /// proportional one. Everything else — ships, stations, debris — keeps the old flat rule.
    /// </remarks>
    private float ClearanceOf(SpaceObj o)
    {
        float r = o.Radius > 0 ? o.Radius : 0f;

        return EntityTypes.Of(o.Id) switch
        {
            // The server builds an asteroid's collider as a sphere of radius * 0.9 (bsgocore
            // SpaceObjectFactory.createAsteroid), so the published radius is generous already and
            // the margin only has to cover our own hull.
            SpaceEntityType.Asteroid =>
                r * AsteroidColliderFactor + Math.Max(AsteroidCollisionMargin, MyRadius),

            // Proportional, because a planetoid's published radius is the one figure that is
            // definitely not conservative, and hitting one is not a scrape.
            SpaceEntityType.Planetoid =>
                r * PlanetoidClearanceFactor + Math.Max(PlanetoidCollisionMargin, MyRadius * 2f),

            _ => r + SafetyMargin,
        };
    }

    /// <summary>The server's own asteroid collider is <c>radius * 0.9</c>. Not a tunable: it is
    /// a fact about the server, and pretending a rock is bigger than the thing that can hit us is
    /// what the margin is for.</summary>
    private const float AsteroidColliderFactor = 0.9f;

    /// <summary>
    /// Room to leave around a solid body, on top of its own radius.
    ///
    /// The radius on the wire is very close to the real thing — the server builds an asteroid's
    /// collider as a sphere of <c>radius * 0.9</c> (bsgocore <c>SpaceObjectFactory.createAsteroid</c>)
    /// — so the margin is the ship's problem, not the rock's. A flat 130u was most of a small
    /// rock's no-go zone all by itself: a 38u asteroid became a 168u sphere, four times its own
    /// size, and in a belt of those the ship is permanently inside somebody's.
    ///
    /// Floored by twice our own hull, which is the number the margin is actually for.
    /// </summary>
    private float SafetyMargin => Math.Max(CollisionMargin, MyRadius * 2f);

    /// <summary>Our own half-size, from our hull's World card. 0 until the card arrives.</summary>
    private float MyRadius
    {
        get
        {
            uint guid = _world.Get(_world.MyObjectId)?.CardGuid ?? 0;
            return guid == 0 ? 0f : Cards.World(guid)?.Radius ?? 0f;
        }
    }

    /// <summary>One line per obstacle, not one per tick — a dodge lasts several seconds and the
    /// log is meant to be readable.</summary>
    private void NoteDodge(SpaceObj blocker, float gap, DateTime now)
    {
        if (_dodgeId == blocker.Id && (now - _dodgeSince).TotalSeconds < 10) return;
        _dodgeId = blocker.Id;
        _dodgeSince = now;
        NearMisses++;

        // The two states read identically in the old wording — "0u of room left ahead" was
        // printed both for a rock we were about to hit and for one we were already inside, which
        // are opposite problems with opposite cures.
        Log?.Invoke(blocker.Id == _escapeFrom
            ? $"Inside {blocker}'s clearance ({ClearanceOf(blocker):F0}u) — backing out at speed."
            : $"Braking and steering around {blocker} — {gap:F0}u of room left ahead.");
    }

    /// <summary>
    /// Circles a body at a set radius, at running speed.
    ///
    /// This is what waiting at a refuge looks like when something is shooting at us. Parking is
    /// the obvious thing to do at a door and the wrong one: a stationary ship is the easiest shot
    /// in the game, and the dock we are waiting on may be on a cooldown of tens of seconds after
    /// combat. Circling keeps the outpost's guns between us and the threat, keeps us inside dock
    /// range so every retry stays valid, and costs nothing but tylium.
    ///
    /// Docking while moving is fine, and that is measured rather than assumed: a manual dock
    /// landed from 791u while the ship was under way, and the client's own <c>CanDock</c> tests
    /// only relation and range — there is no speed condition anywhere in it.
    /// </summary>
    private async Task OrbitAsync(SpaceObj centre, float radius, DateTime now)
    {
        var radial = _world.MyPosition - centre.PredictedPosition(now);
        float dist = radial.Length();

        // Sitting on top of it. Any direction will do, and outward is the useful one.
        if (dist < 1f) { await RunInDirection(new Vector3(1f, 0f, 0f), now); return; }

        var outward = radial / dist;

        // The plane to circle in. Crossing with world up gives a level orbit; directly above or
        // below the body that product collapses, and the heading would be noise rather than a
        // direction, so fall back to another axis.
        var tangent = Vector3.Cross(outward, new Vector3(0f, 1f, 0f));
        if (tangent.LengthSquared() < 0.01f)
            tangent = Vector3.Cross(outward, new Vector3(1f, 0f, 0f));
        tangent = Vector3.Normalize(tangent);

        // A pure tangent is a chord, not an arc, so the circle widens every tick and the ship
        // spirals out of dock range. The radial term is what closes it back up: positive error
        // means too far out, and subtracting `outward` turns us in.
        float error = Math.Clamp((dist - radius) / Math.Max(radius, 1f), -1f, 1f);

        await RunInDirection(tangent - outward * error, now);
    }

    /// <summary>
    /// Where to circle a station: outside the clearance the collision code would fight us over,
    /// inside the range a dock request is still valid from.
    /// </summary>
    private float OrbitRadius(SpaceObj station)
    {
        float inner = ClearanceOf(station) * StandoffMargin;
        float outer = DockRange(station);
        return outer > inner ? (inner + outer) * 0.5f : inner;
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
            // Same rule as the approach: inside something, the throttle is what gets us out.
            throttle = blocker.Id == _escapeFrom
                ? EscapeThrottle(blocker, now)
                : ThrottlePastObstacle(blocker, gap, brakeZone);

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

        // Drop whatever we were shooting — but NOT a lock we hold on the refuge itself, which is
        // what the dock needs. This used to clear both unconditionally on every tick of the
        // retreat, which is how the bot came to ask a server to dock a station it had never told
        // that server it had selected. See LockBeforeDockAsync.
        lock (_gate)
        {
            _target = 0;
            if (_lockedTarget != (refuge?.Id ?? 0)) _lockedTarget = 0;
        }

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

            // Arrived.
            if (_dockTryId != refuge.Id)
            {
                _dockTryId = refuge.Id;
                _dockTrySince = now;
                _refugeHullBest = hull;
            }

            // Is being here working? That is a measurement, not a clock.
            //
            // The first version of this gave up after a flat 10s, which is wrong in the one case
            // it was written for: a dock cooldown after combat can be tens of seconds, and a timer
            // that short abandons a perfectly good outpost while the countdown is still ticking.
            // What actually matters is whether the hull is holding. Under an outpost's guns, with
            // the ship circling rather than parked, it should be — and while it is, there is no
            // reason to be anywhere else however long the door takes.
            if (hull > _refugeHullBest) _refugeHullBest = hull;

            bool bleeding = hull < _refugeHullBest - RefugeBleedFraction;

            if (threat is not null && bleeding && !DockCountdownRunning
                && (now - _dockTrySince).TotalSeconds > DockGiveUpSeconds)
            {
                lock (_gate) _dockRefused.Add(refuge.Id);
                Log?.Invoke($"{refuge} is not taking us in and the hull has fallen from "
                          + $"{_refugeHullBest:P0} to {hull:P0} with {threat} still on us — "
                          + "running instead. It will not be treated as a refuge again this sector.");
                _dockTryId = 0;
                refuge = null;
            }
        }

        // Re-tested rather than nested: the block above can give up on its refuge, and what
        // follows is then the correct behaviour for having none at all — which is to run.
        if (refuge is not null)
        {
            float gap = _world.DistanceToMe(refuge) ?? float.MaxValue;
            string chased = threat is not null
                ? $", {threat} {_world.DistanceToMe(threat) ?? 0f:F0}u behind"
                : "";

            // Waiting at the door, one of two ways. With something shooting at us, circle it: a
            // parked ship is the easiest shot in the game, and this is the state that got one
            // killed. With nothing chasing, park — it costs no tylium and the hull comes back
            // just as fast.
            if (threat is not null)
            {
                float ring = OrbitRadius(refuge);
                await OrbitAsync(refuge, ring, now);
                _orbiting = true;
            }
            else
            {
                await StopThrottleIfMoving();
                _orbiting = false;
            }

            string holding = _orbiting
                ? $"circling at {_world.DistanceToMe(refuge) ?? 0f:F0}u"
                : $"holding at {gap:F0}u";

            if (!AllowDocking)
            {
                if (!_dockDisabledSaid)
                {
                    _dockDisabledSaid = true;
                    Log?.Invoke($"At {refuge} ({gap:F0}u) — holding here rather than docking. "
                              + "Docking is off because every request the bot has sent dropped "
                              + "the session; the outpost's guns are the point anyway. Set "
                              + "AllowDocking in bot.json to try it.");
                }
                Status = $"HULL {hull:P0} — sheltering at {refuge}, {holding}, not docking{chased}";
                return;
            }

            // Select it first — the difference between a dock and a dropped session. Costs one
            // tick, which we are spending circling anyway.
            if (await LockBeforeDockAsync(refuge))
            {
                Status = $"HULL {hull:P0} — at {refuge}, {holding}, selecting it to dock{chased}";
                return;
            }

            // Rate-limited like every other dock request: an over-range attempt is logged as
            // cheating with your player id on it. And never while the server's own countdown is
            // running — the client's dock button is disabled for exactly that window, so a
            // request inside it is one the real client could not have produced.
            if (!DockCountdownRunning && (now - _dockAsked).TotalSeconds >= 4)
            {
                _dockAsked = now;
                await _act.Dock(refuge.Id);
                Log?.Invoke($"Retreat: dock requested at #{refuge.Id:X8} from {gap:F0}u"
                          + (_orbiting ? " while circling it." : "."));
            }
            Status = $"HULL {hull:P0} — docking at {refuge}, {holding}"
                   + (DockCountdownRunning
                        ? $", countdown {(_dockCountdownUntil - now).TotalSeconds:F0}s"
                        : "")
                   + chased;
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
        Status = $"HULL {hull:P0} — RUNNING from {threat} ({away:F0}u), {SpeedInGear(_gear):F0}u/s {_gear}";
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
    private float DockRange(SpaceObj station)
    {
        // The card, when we have it: OwnerCard.DockRange is the exact figure the client tests
        // against in CanDock, so nothing else can beat it. 90% of it, for the same reason the
        // learned range is discounted — the distance is measured a tick before the request lands.
        if (DockCard(station)?.DockRange is > 0 and var published)
            return published * 0.9f;

        if (_learnedDockRange > 0) return _learnedDockRange * 0.9f;

        return Math.Max(DockApproach, station.Radius * RadiusClearance + MinimumStandoff);
    }

    /// <summary>The Owner card for an object, which is where dockability actually lives.</summary>
    private OwnerCardInfo? DockCard(SpaceObj o) => o.CardGuid == 0 ? null : Cards.Owner(o.CardGuid);

    /// <summary>
    /// Somewhere we can actually dock, as opposed to something shaped like it.
    ///
    /// <see cref="EntityTypes.IsDockable"/> is a guess from the object id — Outpost or Cruiser —
    /// and it only narrows the search. The Owner card answers it outright, so when we hold one it
    /// wins: a friendly Cruiser whose card says <c>IsDockable == false</c> is not a refuge, and a
    /// retreat that treats it as one is a retreat that flies to a body it can never enter.
    ///
    /// Without a card we fall back to the type, because refusing to retreat to an outpost whose
    /// card has not arrived yet is worse than trying one that turns out to be shut. What makes
    /// that survivable is <see cref="FleeTick"/> no longer parking at zero throttle to find out.
    /// </summary>
    private bool CanDockAt(SpaceObj o)
    {
        if (!EntityTypes.IsDockable(o.Id) || o.Cloaked) return false;
        if (_world.RelationTo(o.Id) is not (Relation.Friend or Relation.Self)) return false;

        // Tried, flown to, and it did not open. Measured beats both the card and the type.
        lock (_gate) if (_dockRefused.Contains(o.Id)) return false;

        return DockCard(o)?.IsDockable ?? true;
    }

    /// <summary>Nearest friendly place to dock, with no opinion about which way the threat is.</summary>
    private SpaceObj? NearestRefuge() =>
        FleeToOutpost ? _world.Nearest(CanDockAt) : null;

    private SpaceObj? SafeOutpost(SpaceObj threat)
    {
        if (!FleeToOutpost || !_world.MyPositionKnown) return null;

        var me = _world.MyPosition;
        var toThreat = threat.PredictedPosition(DateTime.UtcNow) - me;
        if (toThreat.LengthSquared() < 1f) return null;
        toThreat = Vector3.Normalize(toThreat);

        return _world.Snapshot()
            .Where(o => o.HasPosition && CanDockAt(o))
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
    private bool WatchApproach(uint id, float distance, DateTime now, bool detouring = false)
    {
        if (_approachId != id)
        {
            _approachId = id;
            _approachSince = now;
            _approachBestDistance = distance;
            _detourSince = DateTime.MinValue;
            return false;
        }

        if (distance < _approachBestDistance - 1f)
        {
            _approachBestDistance = distance;
            _approachSince = now;
            _detourSince = DateTime.MinValue;
            return false;
        }

        // Flying around an obstacle is progress; it just isn't progress towards the target yet.
        // A detour holds the straight-line distance flat for as long as it takes to clear, and
        // counting that as a stall meant the bot abandoned — and then skipped for two minutes —
        // every rock it had to steer around. Enough of those and nothing is left to mine.
        //
        // But only for as long as a detour could plausibly still be running. This reset used to be
        // unconditional, which made the watchdog unable to fire at all while anything was being
        // dodged — and a rock behind a planetoid is dodged continuously. The one mechanism that
        // breaks a stuck approach was being held down by the very situation it exists to catch,
        // so the ship circled a body it could not get around until someone noticed.
        if (detouring)
        {
            if (_detourSince == DateTime.MinValue) _detourSince = now;
            if ((now - _detourSince).TotalSeconds < DetourPatienceSeconds)
            {
                _approachSince = now;
                return false;
            }
        }
        else
        {
            _detourSince = DateTime.MinValue;
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
        lines.Add($"condition      {(Condition is { } cond
            ? $"{cond.Now:F0} / {cond.Max:F0} ({cond.Now / cond.Max:P0})"
            : _world.MyCondition is { } bare ? $"{bare:F0} (no ship card yet)" : "unknown")}");
        lines.Add($"hangar         {(_hangarSince is { } inHangar
            ? $"out of sector for {(DateTime.UtcNow - inHangar).TotalSeconds:F0}s"
              + (AutoUndock ? $", {_launchAsks} launch ask(s)" : ", auto undock OFF")
            : _world.Anchored
                ? $"anchored to #{_world.AnchoredTo:X8} — riding, {_unanchorAsks} launch ask(s)"
                : "flying")}");
        lines.Add($"deaths         {Deaths}, {RepairsBought} repair(s) bought");

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
              + (UseBoost
                    ? $", engaged past {BoostRunway(AsteroidStandoff):F0}u on a rock"
                      + $" ({Math.Clamp(BoostSpeed * BrakingSeconds, MinBrakeDistance, BrakingDistance):F0}u to brake"
                      + $" + {BoostSpeed * BoostShedSeconds:F0}u to shed it)"
                    : ", toggle is OFF")
            : $"unusable — no BoostSpeed ({boostSource}), so the gear is never engaged"));
        // The EFFECTIVE speed, not the stored throttle. In boost gear the throttle number does
        // nothing — printing it read "52u/s in Boost" while the ship was genuinely doing 86.
        lines.Add($"flying         {(_throttleOpen
            ? $"{SpeedInGear(_gear):F0}u/s in {_gear}"
              + (_gear == Gear.Boost ? $" ({_throttle:F0}u/s stored for Regular)" : "")
            : "stopped")}");
        // Where the ship IS, as opposed to where dead reckoning has got to. Every distance the
        // bot acts on is measured from this, so its age is the error bar on all of them.
        double fixAge = _world.MyFixAgeSeconds;
        lines.Add($"position fix   " + (double.IsPositiveInfinity(fixAge) || fixAge > 1e6
            ? "never stated by the server — everything is dead reckoning"
            : $"{fixAge:F1}s old"
              + (SelfPositionSuspect ? ", FLOWN SINCE — distances unproven" : ", trusted")
              + $", {PositionResyncs} stop(s) to re-confirm"));
        lines.Add("");

        var guns = Weapons.For(WeaponRole.Combat);
        var (mineGuns, improvised) = MiningWeapons();
        lines.Add($"combat reach   {(guns.Count == 0 ? "no weapon known" : $"{EffectiveRange(guns):F0}u, sit at {PreferredRange(guns, CloseInFactor):F0}u + target size")}");
        lines.Add($"hold off       asteroid {AsteroidStandoff:F0}u, planetoid {PlanetoidStandoff:F0}u");

        if (AvoidCollisions)
        {
            var ahead = BlockerAhead(_world.MyVelocity, TopSpeed * CollisionLookaheadSeconds,
                                     CurrentTarget, DateTime.UtcNow, out float room);
            lines.Add($"clearance      asteroid r×{AsteroidColliderFactor:F2} +{AsteroidCollisionMargin:F0}u, "
                    + $"planetoid r×{PlanetoidClearanceFactor:F2} +{PlanetoidCollisionMargin:F0}u, "
                    + $"other r +{SafetyMargin:F0}u (hull {MyRadius:F0}u)");
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
            // Counted once. Each call is a full sweep of the sector, and this line used to make
            // two of them to print one number.
            int confirmed = ConfirmedRocks(nowD);
            string gate = !ScannerAnswering
                ? $"NOT ANSWERING — {_scansWithoutReply} casts with no reply, mining unfiltered"
                : ScanOnlyWhenFiltering && !Filtering
                    ? "idle (no resource filter set)"
                    : confirmed >= ScanQueueDepth
                        ? $"idle (queue full — {confirmed} confirmed, wants {ScanQueueDepth})"
                        : CanAffordScan(scanner) ? "ready" : "waiting for power";
            string kind = scanner.Area switch
            {
                true => "area",
                false => "single-target",
                null => "area unknown — scan once by hand to settle it",
            };
            lines.Add($"scanner        ability #{scanner.AbilityId}, {kind}, reach "
                    + $"{scanner.MaxRange ?? FallbackRange:F0}u, "
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
        lines.Add($"mining for     {(!Filtering
            ? "any resource"
            : string.Join(" > ", WantedResources) + "   (best first)")}");

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

        AddCatalogueLines(lines, objs);

        lines.Add("");
        foreach (var line in Fights.Describe()) lines.Add(line);

        var fought = Fights.Classes();
        if (fought.Count > 0)
        {
            lines.Add("");
            lines.Add("fought");
            foreach (var line in Fights.DescribeClasses()) lines.Add(line);
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// What the server's own catalogue has told us, and what it says about what is in front of
    /// us right now.
    ///
    /// The per-contact block is the one to read: it is the difference between "an enemy" and
    /// "a tier 3 gunship with 4,100 hull and 210 avoidance", which is the whole reason for
    /// reading cards rather than inferring from damage taken.
    /// </summary>
    private void AddCatalogueLines(List<string> lines, List<SpaceObj> objs)
    {
        lines.Add("");
        foreach (var line in Cards.Describe()) lines.Add(line);

        if (!FetchCatalogue) lines.Add("               (requests off — passive sniffing only)");

        // Only hostiles, and only ones we can actually see: the point is the fight in front of
        // us, not a dump of everything ever cached.
        var seen = objs.Where(o => !o.IsMe && o.CardGuid != 0 && o.HasPosition && IsHostile(o))
                       .GroupBy(o => o.CardGuid)
                       .OrderBy(g => _world.DistanceToMe(g.First()) ?? float.MaxValue)
                       .Take(8)
                       .ToList();

        if (seen.Count == 0) return;

        lines.Add("");
        lines.Add("hostiles by class");
        foreach (var group in seen)
        {
            var sample = group.First();
            var ship = Cards.Ship(group.Key);
            var world = Cards.World(group.Key);

            string name = world?.PrefabName is { Length: > 0 } p ? p : $"card {group.Key}";
            string count = group.Count() > 1 ? $" x{group.Count()}" : "";

            if (ship is null)
            {
                lines.Add($"  {name}{count} — card not fetched yet");
                continue;
            }

            var guns = Cards.WeaponsOf(group.Key);
            string arms = guns.Count == 0
                ? "armament not resolved yet"
                : $"{guns.Count} weapon(s), "
                + $"{guns.Sum(g => g.Dps ?? 0f):F0} dps, reach {guns.Max(g => g.MaxRange ?? 0f):F0}u";

            lines.Add($"  T{ship.Tier} {name}{count} — hull {ship.MaxHull?.ToString("F0") ?? "?"}"
                    + $", avoid {ship.Avoidance?.ToString("F0") ?? "?"}"
                    + $", armor {ship.Armor?.ToString("F0") ?? "?"}"
                    + $", {ship.RoleText}");
            lines.Add($"      {arms}");
        }
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
