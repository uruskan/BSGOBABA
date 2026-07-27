using System.Numerics;
using BsgoBot.Cards;
using BsgoBot.Net;
using BsgoBot.Protocol;
using BsgoBot.Proxy;
using BsgoBot.World;

namespace BsgoBot.Bot;

public sealed partial class FarmBot
{
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

}
