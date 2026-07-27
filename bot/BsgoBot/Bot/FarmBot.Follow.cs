using System.Numerics;
using BsgoBot.Cards;
using BsgoBot.Net;
using BsgoBot.Protocol;
using BsgoBot.Proxy;
using BsgoBot.World;

namespace BsgoBot.Bot;

public sealed partial class FarmBot
{
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

}
