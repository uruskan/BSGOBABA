using System.Numerics;
using BsgoBot.Cards;
using BsgoBot.Net;
using BsgoBot.Protocol;
using BsgoBot.Proxy;
using BsgoBot.World;

namespace BsgoBot.Bot;

public sealed partial class FarmBot
{
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
            if (T.TopSpeedOverride > 0f) return T.TopSpeedOverride;

            float stat = _world.ShipStat(ObjectStat.Speed) ?? 0f;
            if (stat > 0f) return stat;

            return Math.Max(_observedTopSpeed, T.FallbackSpeed);
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
            if (T.BoostSpeedOverride > 0f) return T.BoostSpeedOverride;
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
        float brake = Math.Clamp(BoostSpeed * T.BrakingSeconds, T.MinBrakeDistance, T.BrakingDistance);
        return stopRange + brake + BoostSpeed * T.BoostShedSeconds;
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
        var gear = T.UseBoost && BoostSpeed > 0f && distance > BoostRunway(stopRange)
            ? Gear.Boost
            : Gear.Regular;
        float flying = SpeedInGear(gear);

        // The zone is how far this ship travels in T.BrakingSeconds, not a flat number, and it is
        // no longer widened by the standoff. `Max(700, stopRange)` meant a 179u hold on a rock
        // started braking at 879u and crawled the last stretch at T.MinApproachSpeed — around a
        // minute of creeping across ground the ship could cover in seconds.
        float brakeZone = Math.Clamp(flying * T.BrakingSeconds, T.MinBrakeDistance, T.BrakingDistance);

        // Look no further than the target itself: something past it is not in the way.
        float lookahead = Math.Min(distance, Math.Max(flying * T.CollisionLookaheadSeconds, brakeZone * 2f));
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
            throttle = Math.Max(TopSpeed * MathF.Sqrt(t), T.MinApproachSpeed);
        }

        // Brake for what is in the way as well as for what we're aiming at. Turning takes room,
        // and the whole failure this guards against is arriving at an obstacle with the throttle
        // still set for a target thousands of units behind it. Boost is off outright: there is no
        // approach worth being unable to turn out of.
        if (blocker is not null)
        {
            // Getting OUT of something outranks arriving at something. The old line took the
            // minimum of the two, so a ship inside a big rock's clearance sphere — gap 0, taper
            // 0, throttle pinned to T.MinApproachSpeed — crawled its way out over tens of seconds
            // while the mining brake held it down as well. That is the wedge: the one state where
            // the ship is definitely in the wrong place is the state it left at walking pace.
            // Braking buys no damage relief — the collision formula has no speed term — so it is
            // worth doing only when it buys the seconds the turn needs. On a rock the deflection
            // already clears, slowing down is pure lost travel, and travel is most of the clock.
            throttle = blocker.Id == _escapeFrom
                ? EscapeThrottle(blocker, now)
                : BrakingBuysTheTurn(blocker, gap)
                    ? Math.Min(throttle, ThrottlePastObstacle(blocker, gap, brakeZone))
                    : throttle;

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

        if (!T.AvoidCollisions || !_world.MyPositionKnown || heading.LengthSquared() < 1e-4f) return null;

        var me = _world.MyPosition;
        var dir = Vector3.Normalize(heading);

        SpaceObj? nearest = null;
        float nearestAlong = 0f;

        foreach (var o in _world.Snapshot())
        {
            if (o.IsMe || o.Id == ignoreId || o.Id == _world.MyObjectId) continue;
            if (!o.HasPosition || !EntityTypes.IsSolid(o.Id)) continue;

            // Measure whatever we can see, so the radius-to-hull conversion keeps improving.
            NoteRockSize(o);

            // Cheap enough to hit that flying round it costs more than the hull it saves. Dropped
            // here rather than later on purpose: an obstacle that never becomes the blocker is one
            // the deflection never bends for AND the throttle never brakes for, which is the whole
            // saving. The one exception is a rock we are already inside — that is an escape, not a
            // dodge, and it has to keep working however small the rock is.
            if (o.Id != _escapeFrom && !WorthDodging(o, out float clipCost))
            {
                NoteSkippedDodge(o, clipCost, now);
                continue;
            }

            var toObs = o.PredictedPosition(now) - me;
            float clear = ClearanceOf(o);

            // Hysteresis on the one we are getting out of: it counts as "inside" until we are
            // clear of it by a margin. Leaving on the exact boundary is what let the ship exit a
            // big rock's sphere and be aimed straight back into it on the very next tick.
            if (o.Id == _escapeFrom) clear *= T.EscapeClearance;

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
        if (blocker.Id == _escapeFrom) clear *= T.EscapeClearance;

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
        return Math.Max(TopSpeed * MathF.Sqrt(t), T.MinApproachSpeed);
    }

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
        return leaving ? TopSpeed : Math.Max(TopSpeed * 0.5f, T.MinApproachSpeed);
    }

    /// <summary>
    /// How much room a solid object needs to be flown PAST, measured from its centre.
    ///
    /// Deliberately not <see cref="BotTuning.RadiusClearance"/>. That multiplier (×3) is a *standoff*
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
    /// <summary>
    /// How big a solid body is, with an assumed size when the server has not said.
    ///
    /// <para>Radius arrives in one message only — <c>Reply.WhoIs</c> — and for a planetoid it
    /// routinely never arrives at all, because the bot sees whatever WhoIs bodies the game client
    /// happened to ask for and the client does not ask about scenery it is already drawing.
    /// <see cref="ClearanceOf"/> used to read that silence as <c>radius 0</c>, which turned a body
    /// a couple of thousand units across into a 500u sphere: the margin alone.</para>
    ///
    /// <para>The logs say exactly that. Every "room left ahead" figure against a planetoid sits at
    /// 478-501u, pinned to <see cref="BotTuning.PlanetoidCollisionMargin"/>, with the low ones at
    /// 54u and 59u — the ship threading what it thought was empty space and finding the surface.
    /// That is the reported flying-through-planetoids.</para>
    ///
    /// <para>An unknown size is therefore assumed LARGE, and <see cref="AskUnknownSizesAsync"/>
    /// asks the server for the real figure so the guess is temporary. Being wrong in this
    /// direction costs a wider berth around one body; being wrong the other way costs the ship.</para>
    /// </summary>
    private float RadiusOf(SpaceObj o)
    {
        if (o.Radius > 0) return o.Radius;

        return EntityTypes.Of(o.Id) switch
        {
            SpaceEntityType.Planetoid or SpaceEntityType.Planet => T.PlanetoidAssumedRadius,
            SpaceEntityType.Asteroid => T.AsteroidAssumedRadius,
            _ => 0f,
        };
    }

    private float ClearanceOf(SpaceObj o)
    {
        float r = RadiusOf(o);

        return EntityTypes.Of(o.Id) switch
        {
            // The server builds an asteroid's collider as a sphere of radius * 0.9 (bsgocore
            // SpaceObjectFactory.createAsteroid), so the published radius is generous already and
            // the margin only has to cover our own hull.
            SpaceEntityType.Asteroid =>
                r * AsteroidColliderFactor + Math.Max(T.AsteroidCollisionMargin, MyRadius),

            // Proportional, because a planetoid's published radius is the one figure that is
            // definitely not conservative, and hitting one is not a scrape.
            SpaceEntityType.Planetoid =>
                r * T.PlanetoidClearanceFactor + Math.Max(T.PlanetoidCollisionMargin, MyRadius * 2f),

            _ => r + SafetyMargin,
        };
    }

    /// <summary>Solid bodies we have asked the server to describe, so one unsized planetoid does
    /// not produce a WhoIs every tick.</summary>
    private readonly Dictionary<uint, DateTime> _sizeAsked = new();

    /// <summary>
    /// Ask the server how big the solid things around us actually are.
    ///
    /// <c>GameActions.WhoIs</c> has existed since the first build and nothing ever called it — the
    /// bot read whatever WhoIs replies the client's own curiosity produced. That is fine for
    /// ships, which the client subscribes to anyway, and useless for scenery: the client already
    /// has the planetoid's model and never asks. So the one number that decides whether the ship
    /// steers around a planetoid or into it was left to chance.
    ///
    /// A few per sweep, once a minute each, and only while collision avoidance is on.
    /// </summary>
    private async Task AskUnknownSizesAsync(DateTime now)
    {
        if (!T.AvoidCollisions) return;

        int asked = 0;
        foreach (var o in _world.Snapshot())
        {
            if (asked >= 3) break;
            if (!o.HasPosition || o.Radius > 0 || !EntityTypes.IsSolid(o.Id)) continue;

            lock (_gate)
            {
                if (_sizeAsked.TryGetValue(o.Id, out var at) && (now - at).TotalSeconds < 60) continue;
                _sizeAsked[o.Id] = now;
            }

            await _act.WhoIs(o.Id);
            asked++;
        }
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
    private float SafetyMargin => Math.Max(T.CollisionMargin, MyRadius * 2f);

    /// <summary>Our own half-size, from our hull's World card. 0 until the card arrives.</summary>
    private float MyRadius
    {
        get
        {
            uint guid = _world.Get(_world.MyObjectId)?.CardGuid ?? 0;
            return guid == 0 ? 0f : Cards.World(guid)?.Radius ?? 0f;
        }
    }

    // ------------------------------------------------------------------ what a clip actually costs

    /// <summary>
    /// Observed hull points per unit of asteroid radius, newest last. Capped, because this only
    /// needs to be roughly right and an unbounded list on a 250ms loop is a leak.
    /// </summary>
    private readonly List<float> _rockHpPerRadius = [];

    /// <summary>Rocks already sampled, so one rock cannot dominate the estimate by being
    /// re-measured on every tick it stays subscribed.</summary>
    private readonly HashSet<uint> _rockHpSampled = [];

    /// <summary>
    /// Learns how much hull an asteroid carries per unit of radius.
    ///
    /// Needed because hull points arrive only for objects we have <b>subscribed</b> to, which in
    /// practice means the rock we are mining and nothing else. Every other rock in the belt — the
    /// ones actually in the way — publishes a radius and nothing more. So the radius has to stand
    /// in for the hull, and the conversion between them is measured off the rocks we do know
    /// rather than invented.
    /// </summary>
    private void NoteRockSize(SpaceObj o)
    {
        if (!o.StatsKnown || o.Hull <= 0f || o.Radius <= 0f) return;
        if (o.Type != SpaceEntityType.Asteroid) return;
        lock (_gate)
        {
            if (!_rockHpSampled.Add(o.Id)) return;
            _rockHpPerRadius.Add(o.Hull / o.Radius);
            if (_rockHpPerRadius.Count > 200) _rockHpPerRadius.RemoveAt(0);
        }
    }

    /// <summary>How many rocks must have been measured before the estimate is trusted at all.</summary>
    private const int RockSizeSamplesNeeded = 5;

    /// <summary>
    /// Hull points we believe an asteroid has, or null when there is no honest way to say.
    ///
    /// A rock we have stats for answers for itself. Everything else is its radius times a high
    /// percentile of what rocks have measured so far — high on purpose, because the whole point of
    /// the number is deciding what is safe to hit, and the expensive direction to be wrong in is
    /// guessing a rock is small.
    /// </summary>
    private float? EstimatedRockHull(SpaceObj o)
    {
        if (o.StatsKnown && o.Hull > 0f) return o.Hull;
        if (o.Radius <= 0f) return null;

        lock (_gate)
        {
            if (_rockHpPerRadius.Count < RockSizeSamplesNeeded) return null;
            var sorted = _rockHpPerRadius.Order().ToList();
            float p90 = sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * 0.9f))];
            return o.Radius * p90;
        }
    }

    /// <summary>
    /// Hull points a collision with this object would cost us, or null if unknown.
    ///
    /// The server charges <c>0.5 * the asteroid's max hull points</c>, reduced by armour only
    /// where armour exceeds the collision's armour piercing of 50 — which a 40-armour line hull
    /// does not, so it pays the full price (bsgocore <c>DamageCalculator.calculateDamageFromCollision</c>).
    ///
    /// <para><b>There is no speed term.</b> Drifting into a rock at walking pace costs exactly
    /// what ramming it at full boost costs. Braking for an obstacle therefore buys no damage
    /// reduction whatsoever — its only value is the extra seconds it gives the ship to turn.</para>
    ///
    /// <para>Reads low for a rock we have already mined into: the server uses the rock's MAX hull
    /// and this can only see what is left of it. That errs towards flying through a rock we have
    /// been shooting, which is the one we are parked next to anyway.</para>
    /// </summary>
    private float? CollisionCost(SpaceObj o) =>
        EstimatedRockHull(o) is { } hp ? 0.5f * hp : null;

    /// <summary>
    /// Whether this obstacle is worth the detour, or cheaper to simply hit.
    ///
    /// Only asteroids are ever waved through. A planetoid, a station or another ship is either far
    /// too expensive or not covered by the collision formula at all.
    ///
    /// The comparison is against our own hull, so it scales itself across ships without a second
    /// setting: five percent of a Raptor is a pebble, five percent of a Vanir is a respectable
    /// rock — and the Vanir regenerates it in a few seconds, while turning a 27 m/s hull at 22
    /// degrees a second around the same rock costs far longer than that.
    /// </summary>
    private bool WorthDodging(SpaceObj o, out float cost)
    {
        cost = 0f;
        if (T.IgnoreCollisionHullFraction <= 0f) return true;
        if (o.Type != SpaceEntityType.Asteroid) return true;

        // Unknown cost is dodged. Never skip on a guess.
        if (CollisionCost(o) is not { } predicted) return true;
        if (_world.MyMaxHull is not { } maxHull || maxHull <= 0f) return true;

        cost = predicted;
        return predicted > maxHull * T.IgnoreCollisionHullFraction;
    }

    /// <summary>Obstacles we have decided to fly through, so the log gets one line each rather
    /// than one per tick.</summary>
    private readonly Dictionary<uint, DateTime> _clipNoted = new();

    private void NoteSkippedDodge(SpaceObj rock, float cost, DateTime now)
    {
        lock (_gate)
        {
            if (_clipNoted.TryGetValue(rock.Id, out var at) && (now - at).TotalSeconds < 30) return;
            _clipNoted[rock.Id] = now;
            if (_clipNoted.Count > 200)
                foreach (var stale in _clipNoted.Where(k => (now - k.Value).TotalMinutes > 5)
                                                .Select(k => k.Key).ToList())
                    _clipNoted.Remove(stale);
        }

        ClipsAllowed++;
        float hull = _world.MyMaxHull ?? 0f;
        Log?.Invoke($"Not dodging {rock} (r{rock.Radius:F0}u) — a clip costs about {cost:F0} hull"
                  + (hull > 0 ? $", {cost / hull:P1} of ours" : "")
                  + (rock.StatsKnown ? " (measured)" : " (estimated from radius)")
                  + ". Turning around it costs more.");
    }

    /// <summary>Obstacles waved through this session, for the diagnostics dump.</summary>
    public int ClipsAllowed { get; private set; }

    /// <summary>
    /// Degrees per second the hull turns at, as published. Null when the server never said.
    /// </summary>
    private float? TurnSpeed => _world.ShipStat(ObjectStat.TurnSpeed) is > 0 and var t ? t : null;

    /// <summary>
    /// Whether slowing down would actually help us miss this, as opposed to merely arriving late.
    ///
    /// Braking cannot reduce collision damage — see <see cref="CollisionCost"/> — so the only
    /// reason to do it is to buy the seconds a turn needs. That makes it a comparison: the time
    /// left before we reach the obstacle against the time it takes to swing the nose far enough
    /// to clear it. If the turn already fits, braking is pure lost travel.
    ///
    /// Unknown turn rate keeps the old unconditional braking, because a guess here is a ram.
    /// </summary>
    private bool BrakingBuysTheTurn(SpaceObj blocker, float gap)
    {
        if (TurnSpeed is not { } degPerSec) return true;

        float speed = Math.Max(TopSpeed, 1f);
        float secondsToImpact = gap / speed;

        // How far off the nose the obstacle's edge sits, from where we are now. Small angles for
        // something dead ahead and far off, which is exactly the case a turn clears easily.
        float along = gap + ClearanceOf(blocker);
        float needDegrees = MathF.Atan2(ClearanceOf(blocker), Math.Max(along, 1f)) * (180f / MathF.PI);
        float secondsToTurn = needDegrees / degPerSec;

        // Braking only earns its keep when the turn does not already fit, with margin.
        return secondsToTurn > secondsToImpact * 0.75f;
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
        float inner = ClearanceOf(station) * T.StandoffMargin;
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
        var gear = T.UseBoost && BoostSpeed > 0f ? Gear.Boost : Gear.Regular;
        float flying = SpeedInGear(gear);

        float brakeZone = Math.Clamp(flying * T.BrakingSeconds, T.MinBrakeDistance, T.BrakingDistance);
        float lookahead = Math.Max(flying * T.CollisionLookaheadSeconds, brakeZone * 2f);

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

            bool bleeding = hull < _refugeHullBest - T.RefugeBleedFraction;

            if (threat is not null && bleeding && !DockCountdownRunning
                && (now - _dockTrySince).TotalSeconds > T.DockGiveUpSeconds)
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

            if (!T.AllowDocking)
            {
                if (!_dockDisabledSaid)
                {
                    _dockDisabledSaid = true;
                    Log?.Invoke($"At {refuge} ({gap:F0}u) — holding here rather than docking. "
                              + "Docking is off because every request the bot has sent dropped "
                              + "the session; the outpost's guns are the point anyway. Set "
                              + "T.AllowDocking in bot.json to try it.");
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

        return Math.Max(T.DockApproach, station.Radius * T.RadiusClearance + T.MinimumStandoff);
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
        T.FleeToOutpost ? _world.Nearest(CanDockAt) : null;

    private SpaceObj? SafeOutpost(SpaceObj threat)
    {
        if (!T.FleeToOutpost || !_world.MyPositionKnown) return null;

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
            if ((now - _detourSince).TotalSeconds < T.DetourPatienceSeconds)
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

}
