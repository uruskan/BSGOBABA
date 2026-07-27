using System.Numerics;
using BsgoBot.Cards;
using BsgoBot.Net;
using BsgoBot.Protocol;
using BsgoBot.Proxy;
using BsgoBot.World;

namespace BsgoBot.Bot;

public sealed partial class FarmBot
{
    // ------------------------------------------------------------------ mining

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
        T.WantedResources.Count > 0 && !Array.TrueForAll(Resources.Minable, T.WantedResources.Contains);

    /// <summary>Whether a scanned rock holds something we asked for.</summary>
    private bool WantsResource(uint guid) =>
        !Filtering || T.WantedResources.Contains((ResourceType)guid);

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
    /// were never asked. They break rocks too, so unless you turn <see cref="BotTuning.FireGunsWhileMining"/>
    /// off, they fire alongside it. FireAll still range-checks each one, so a cannon with a dead
    /// zone at knife range simply skips its turn.
    /// </summary>
    private List<Weapon> MiningFireSet(List<Weapon> lasers, bool improvised)
    {
        // Improvised already IS the combat list, so there is nothing to add.
        if (improvised || !T.FireGunsWhileMining) return lasers;

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

        return Math.Max(Math.Max(T.ScanReachFallback, mining), _scanProvenRange * 1.25f);
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
        if (!T.UseRepairAbility) return;
        if (_world.MyHullFraction is not { } hull || hull >= T.RepairAtHull) return;
        if (_world.MyObjectId == 0) return;

        var now = DateTime.UtcNow;
        foreach (var w in Weapons.For(WeaponRole.Repair))
        {
            double interval = w.Cooldown is { } cd && cd > 0 ? cd * 1000.0 : T.RepairIntervalMs;
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
        double interval = scanner.Cooldown is { } cd && cd > 0 ? cd * 1000.0 : T.ScanIntervalMs;
        if ((now - scanner.LastFired).TotalMilliseconds < interval) return;

        // Nothing downstream reads the answer when we'll mine whatever is nearest anyway.
        //
        // The raw list, not <see cref="Filtering"/>. Ticking all three resources no longer counts
        // as narrowing — correctly, because the guns must not be held for it — but the scan is
        // still read: it carries the ore COUNT, which is what ranks one confirmed rock above
        // another in RockValue. Gating the sweep on the narrowing test would have thrown that away.
        if (T.ScanOnlyWhenFiltering && T.WantedResources.Count == 0) return;

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
        // T.FallbackRange (3000u) and the range you typed into the loadout panel was ignored.
        //
        // Overstating the reach is not harmless: the batch then carries rocks the server
        // considers out of range, it refuses the cast outright, and a refusal is silent. That is
        // the "cast 3 times with no reply — most likely out of power cells" warning, which had
        // nothing to do with power cells.
        //
        // Which is exactly the argument T.RequireKnownReach makes for the guns, so the scanner is
        // held to it too. It used to fall through to T.FallbackRange regardless — aiming on the
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
                .Take(T.MaxAreaScanTargets)
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
        if (strikes >= T.ScanStrikesBeforeGone)
        {
            DropTarget(rock.Id, $"{strikes} scans answered by nothing — the rock is gone",
                       TimeSpan.FromMinutes(T.MuteRockSkipMinutes), hard: true);
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
        if ((now - _lastProbe).TotalSeconds < T.ProbeIntervalSeconds) return;

        Weapon? candidate;
        lock (_gate)
            candidate = Weapons.ProbeCandidates().FirstOrDefault(w => !_probed.Contains(w.AbilityId));
        if (candidate is null) return;

        var rock = _world.Nearest(o => EntityTypes.IsMinable(o.Id) && !o.Scanned);
        if (rock is null) return;

        float reach = _world.SlotStat(candidate.AbilityId, ObjectStat.MaxRange) ?? T.FallbackRange;
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
        return (now - o.ScannedAt).TotalSeconds > T.ScanFreshnessSeconds;
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
                || (now - asked).TotalSeconds > T.ScanRetrySeconds;
    }

    /// <summary>
    /// Whether a scan is worth its power right now.
    ///
    /// The scanner and the lasers draw on the same pool, so every scan is paid for in mining, and
    /// the two scanner shapes want opposite policies.
    ///
    /// An <b>area</b> scanner identifies a field per cast, so it is worth running until a queue
    /// has built up — <see cref="BotTuning.ScanQueueDepth"/> — and pointless after that.
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
            ? ConfirmedRocksNear(now) < T.ScanQueueDepth
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
    /// Both halves matter. A fix older than <see cref="BotTuning.SelfPositionTrustSeconds"/> is not by
    /// itself a problem — a ship parked on a rock for two minutes has a two-minute-old fix and is
    /// exactly where that fix says, because nothing has moved it. Drift is something a flight
    /// does. So the test is "have we flown since the server last told us", not "is the fix old".
    /// </summary>
    private bool SelfPositionSuspect =>
        _world.MyFixAgeSeconds > T.SelfPositionTrustSeconds && _world.MyFixAt < _movedAt;

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
        // T.SelfPositionWaitSeconds. Re-arms by itself as soon as any fix arrives.
        if (_fixWaitGaveUp) return false;

        if (_fixWaitSince == DateTime.MinValue)
        {
            _fixWaitSince = now;
            PositionResyncs++;
        }

        double waited = (now - _fixWaitSince).TotalSeconds;
        if (waited > T.SelfPositionWaitSeconds)
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
            string filter = !Filtering ? "" : $", filtering for {string.Join(" > ", T.WantedResources)}";
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
        float range = lasers.Count > 0 ? EffectiveRange(lasers) : T.FallbackRange;
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
            if (T.AutoApproach)
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
        bool closing = T.AutoApproach && dist > preferred;
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
        if (T.UseMiningFacility && rock.Scanned && rock.IsMinable && _facilityOrdered.Add(rock.Id))
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
                          + $"instead of waiting. Reload its consumable to get {string.Join(" > ", T.WantedResources)} "
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
        // are held for want of a known reach or by T.HoldFireUntilOptimal.
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
                   + (shooting.Any(w => !T.RequireKnownReach || ReachKnown(w))
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
        if (!T.AvoidHostileStations) return;
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
        Log?.Invoke($"{blocked} asteroid(s) are inside the {T.HostileStationKeepOut:F0}u keep-out "
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
        float t = distance / Math.Max(T.RockTravelPenalty, 1f);
        return o.ResourceCount / (1f + t * t);
    }

    /// <summary>
    /// Where to go when nothing passes the normal filter.
    ///
    /// Almost everything <see cref="MiningCandidate"/> rejects is a BELIEF rather than a fact. A
    /// scan saying "empty" or "wrong resource" is up to <see cref="BotTuning.ScanFreshnessSeconds"/> old and
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

        foreach (var want in T.WantedResources)
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
        o.Scanned && (now - o.ScannedAt).TotalSeconds <= T.ScanFreshnessSeconds;

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

}
