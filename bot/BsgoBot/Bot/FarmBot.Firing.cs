using System.Numerics;
using BsgoBot.Cards;
using BsgoBot.Net;
using BsgoBot.Protocol;
using BsgoBot.Proxy;
using BsgoBot.World;

namespace BsgoBot.Bot;

public sealed partial class FarmBot
{
    // ------------------------------------------------------------------ firing

    /// <summary>
    /// The longest reach among the weapons we'd actually use — the distance at which it is
    /// worth opening fire at all. Real numbers when the server publishes slot stats; the
    /// configured fallback when it doesn't.
    /// </summary>
    private float EffectiveRange(List<Weapon> guns)
    {
        var known = guns.Where(w => w.MaxRange is > 0).Select(w => w.MaxRange!.Value).ToList();
        return known.Count > 0 ? known.Max() : T.FallbackRange;
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
        if (T.RequireKnownReach && !ReachKnown(w)) return false;
        if (w.MaxRange is { } max && distance > max) return false;
        if (w.MinRange is { } min && distance < min) return false;
        if (T.HoldFireUntilOptimal && stillClosing
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
                   : T.FallbackRange;

        float want = band * factor;

        var mins = guns.Where(w => w.MinRange is > 0).Select(w => w.MinRange!.Value).ToList();
        if (mins.Count > 0) want = Math.Max(want, mins.Max() * 1.2f);

        return Math.Max(want, T.MinimumStandoff);
    }

    /// <summary>
    /// Where to actually stop, for THIS target.
    ///
    /// Every distance on the wire is centre-to-centre, and an asteroid is not a point — closing
    /// to 150u of the centre of a rock that is hundreds of units across is not a firing position,
    /// it's a collision, and collisions kill. So the object's own radius plus clearance is a
    /// <b>floor</b> under the answer, and weapon reach is the ceiling.
    ///
    /// <para>Between those two the weapon decides, and for anything that holds still the answer
    /// is the top of its optimal band. Accuracy is flat across that band, so closing costs travel
    /// and clearance and buys nothing — which matters most on exactly the ship that can least
    /// afford it, a line hull with long guns threading a rock field.</para>
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
            case SpaceEntityType.Asteroid when T.AsteroidStandoff > 0:
            {
                // A gap to the SURFACE, plus the rock's own radius — not a distance from its
                // centre. Centre-to-centre made the setting meaningless: it was floored by
                // radius + margin, so on any rock bigger than the number you typed the floor won
                // and typing 50 changed nothing you could see.
                float gap = target.Radius + T.AsteroidStandoff;

                // And never exactly ON the clearance sphere, which is what the old
                // Max(standoff, ClearanceOf) produced whenever the floor won. Park on the
                // boundary and the rock counts as "in the way" the instant it stops being the
                // target — one drift, one rotation, one re-target and the ship is suddenly
                // escaping the thing it was mining a moment ago. That is the churn in the log:
                // engage at 168u, then "0u of room left ahead" against the very same rock.
                float rockClear = ClearanceOf(target) * T.StandoffMargin;

                // Both of those are FLOORS — the closest we are willing to get — not the place
                // to aim for. Treating the gap as the destination is what flew a Vanir to 307u
                // of a rock its mining lasers reach at 1,350u: a thousand units of travel, into
                // the middle of a field of solid objects, in a hull that turns at 22 degrees a
                // second, for nothing.
                //
                // Nothing is bought by closing. A rock does not manoeuvre, and the server's hit
                // chance is flat at or below optimal range and only falls off beyond it
                // (HitchanceBasedOnThrottle.getChanceToHit) — so a shot from the edge of the
                // band lands exactly as often as one from arm's length. The band is therefore
                // where to sit: same ore, less travel, and a large hull stays out of the rocks.
                //
                // This is what the static case below has always done. The asteroid branch simply
                // returned before reaching it.
                float floor = Math.Max(gap, rockClear);

                // Only when the band is a real published number. PreferredRange falls back to
                // MAX range when no optimal is known, and max range is the one place that must
                // not be chosen: hit chance is flat up to optimal and falls off past it, so
                // parking at the edge of reach is parking where the shots miss. An unknown
                // optimal therefore keeps the old close-in behaviour, which is merely wasteful
                // rather than useless.
                bool bandKnown = guns.Exists(w => w.OptimalRange is > 0);
                float band = bandKnown ? PreferredRange(guns, 1f) : floor;

                // Reach still caps it, but never below the floor: a firing position we would
                // treat as a collision is not a firing position.
                return Math.Clamp(Math.Max(band, floor), floor, Math.Max(reach * 0.95f, floor));
            }

            // Not clamped to weapon reach: a planetoid is worked by ordering a mining ship, not by
            // shooting it, so there is nothing to stay in range of. It IS floored by the body's
            // own size, because the configured number is a flat 1200u and planetoids are not all
            // smaller than that — holding at 1200u from the centre of something with a 2000u
            // radius is not a standoff, it is a stated intention to fly into it.
            case SpaceEntityType.Planetoid when T.PlanetoidStandoff > 0:
                // The same clearance the collision code uses, so the approach cannot ask for a
                // hold position the avoidance considers a collision — which is a standoff and a
                // dodge pulling against each other for as long as the bot is pointed at it.
                return target.Radius > 0
                    ? Math.Max(T.PlanetoidStandoff, ClearanceOf(target))
                    : T.PlanetoidStandoff;
        }

        // Closing inside the optimal band only buys anything against something that manoeuvres.
        // Anything that sits still gets the full band: same accuracy, more clearance.
        float factor = EntityTypes.IsStatic(target.Id) ? 1f : T.CloseInFactor;
        float want = PreferredRange(guns, factor);

        float clear = target.Radius > 0
            ? target.Radius * T.RadiusClearance + T.MinimumStandoff
            : T.MinimumStandoff;

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
        float reserve = (_world.MyMaxPower ?? 0f) * T.ScanPowerReserve;
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

            if (T.RequireKnownReach && !ReachKnown(w))
                WarnOnce($"ability #{w.AbilityId} has no known reach from stats, the catalogue "
                       + "or your loadout — holding fire rather than guessing.");

            if (!CanEngage(w, distance, stillClosing)) continue;
            if (!CanAfford(w)) continue;

            // Rate cap, and the reason it exists: this used to fire every gun in the same
            // millisecond. A nine-cast burst inside 3ms is not something a person at a keyboard
            // can produce, and the server closed the connection immediately after every one of
            // them. Spreading them over consecutive ticks costs a fraction of a second of
            // damage and stops the bot looking like a packet flood.
            if (w.Kind != WeaponKind.Toggle && castsThisTick >= T.MaxCastsPerTick) continue;

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
                : T.FallbackFireIntervalMs;

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

    /// <summary>
    /// How far off the nose a target sits when that is more than the mining weapons will bear, or
    /// null when it is inside the arc (or we have no way to tell).
    ///
    /// The narrowest fitted arc decides it: a set of guns is only pointed at something when all of
    /// them can see it, and the widest would otherwise excuse the rest sitting idle. Half the card
    /// Angle either side of the nose, which is what the server measures against.
    ///
    /// Silent about it when the arc or our own facing is unknown. Turning the ship on a guess is
    /// how a bot spends a session spinning next to a rock it could already hit.
    /// </summary>
    private float? TargetOutOfArc(SpaceObj target)
    {
        var facing = _world.MyFacing;
        if (facing.LengthSquared() < 0.01f) return null;

        var (guns, _) = MiningWeapons();
        var arcs = guns.Where(w => w.CardAngle is > 0 and < 360f)
                       .Select(w => w.CardAngle!.Value)
                       .ToList();
        if (arcs.Count == 0) return null;

        var toTarget = target.PredictedPosition(DateTime.UtcNow) - _world.MyPosition;
        if (toTarget.LengthSquared() < 1f) return null;

        float degrees = MathF.Acos(Math.Clamp(
            Vector3.Dot(Vector3.Normalize(facing), Vector3.Normalize(toTarget)), -1f, 1f))
            * (180f / MathF.PI);

        return degrees > arcs.Min() / 2f ? degrees : null;
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
                bool shaped = T.Mode == FarmMode.Mining ? EntityTypes.IsMinable(pin) : !EntityTypes.IsMinable(pin);

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

            // Say so. This was silent, and silence is why a rock being abandoned half broken
            // looked identical in the log to one being finished: the only trace was the next
            // "Engaging" line, with no hint that the previous rock had been left, let alone that
            // the damage already put into it was thrown away. A swap away from a live target is
            // one of the few things worth a line every time it happens.
            if (held is not null && !IsCorpse(held))
            {
                string spent = _mineWatchId == current && _mineHull > held.Hull
                    ? $" after {_mineHull - held.Hull:F0} damage into it"
                    : "";
                Log?.Invoke($"Left {held} unfinished{spent} — it no longer qualifies "
                          + $"({(held.HasPosition ? "something else ranks higher" : "position lost")}).");
            }

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
        if (!T.HuntOnlyVisible) return true;

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

    private async Task EnsureSubscribed(uint id)
    {
        if (!T.SubscribeToTarget) return;
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

        if ((now - _mineProgressAt).TotalSeconds < T.MiningStallSeconds) return false;

        // Before condemning it: is it simply off the beam? Every weapon has a firing arc the
        // server enforces (Algorithm3D.isWeaponPositionInRange takes the ability's Angle), and an
        // out-of-arc cast is refused in the same total silence as a cast at a rock that no longer
        // exists. The two are indistinguishable from the reply, so they used to be treated as one
        // — and a perfectly good rock 500u off the starboard side, well inside a 1,350u reach, was
        // written off as gone.
        //
        // A rock this close cannot be reached by shooting harder, only by turning, so say so and
        // let the mining loop point the ship at it rather than throwing it away.
        if (TargetOutOfArc(rock) is { } offBy)
        {
            _mineProgressAt = now;                     // it has had no fair chance yet
            Log?.Invoke($"{rock} is {offBy:F0}° off the nose — outside the mining arc, so every "
                      + "cast is being refused silently. Turning to face it.");
            return false;
        }

        DropTarget(rock.Id, $"{T.MiningStallSeconds:F0}s of firing with no damage dealt and no ore "
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

        if ((now - _holdSince).TotalSeconds < T.HeldFirePatienceSeconds) return false;

        DropTarget(rockId, $"engaged {T.HeldFirePatienceSeconds:F0}s without a single shot possible "
                         + $"— {why}",
                   TimeSpan.FromMinutes(T.MuteRockSkipMinutes), hard: true);
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

}
