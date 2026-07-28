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
public sealed partial class FarmBot
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
        _scansWithoutReply < T.ScanFailuresBeforeUnfiltered || _unansweredRocks.Count < 2;

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

    /// <summary>
    /// Every tunable number and switch, in one swappable object. See <see cref="BotTuning"/>.
    ///
    /// Named for how often it is read rather than what it holds: nearly every decision in the
    /// farm loop consults it, and <c>T.RetreatHull</c> stays out of the way of the code around
    /// it in a manner that <c>Tuning.RetreatHull</c> does not.
    /// </summary>
    public BotTuning T { get; set; } = new();

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

    public bool Enabled { get; private set; }
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

            if (T.FetchCatalogue && _proxy.ClientConnected)
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

        // Compare ratios with ratios. MyHull is in points, so the old `MyHull < T.RetreatHull`
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

        if (_world.MyHullFraction is { } hull && hull < T.RetreatHull)
        {
            if (T.FleeWhenHurt) { await FleeTick(hull); return; }
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

        if (T.AutoLoot) await SweepLootAsync();

        if (T.Mode == FarmMode.Mining)
        {
            // Two channels, not one behaviour.
            //
            // GUNS first, and it does not return: a ship with cannons as well as mining gear
            // answers a drone with the cannons and keeps the lasers on the rock. Firing at two
            // targets in one tick is legal — the server reads each cast's own target list and
            // ignores what is locked — so the old "drop everything and go fight" is only needed
            // when there is nothing separate to shoot with, or the threat is out of reach.
            bool answered = await ReturnFireAsync();

            // HELM. Falling back to the full interrupt is still right when return fire could not
            // take the shot: a strike ship whose cannons ARE its mining guns has nothing to fire
            // without re-aiming, and a threat beyond reach has to be closed on or fled.
            if (!answered && T.DefendSelf && NearestThreat() is not null)
            {
                await CombatTick(IsThreat, "Defending");
                return;
            }

            await MineTick();
        }
        else await CombatTick();
    }

}
