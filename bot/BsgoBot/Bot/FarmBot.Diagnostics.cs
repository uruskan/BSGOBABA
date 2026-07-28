using System.Numerics;
using BsgoBot.Cards;
using BsgoBot.Net;
using BsgoBot.Protocol;
using BsgoBot.Proxy;
using BsgoBot.World;

namespace BsgoBot.Bot;

public sealed partial class FarmBot
{
    // ------------------------------------------------------------------ loot

    /// <summary>Wrecks and cargo don't come to you — ask for anything already within reach.</summary>
    private async Task SweepLootAsync()
    {
        foreach (var o in _world.Snapshot())
        {
            if (!EntityTypes.IsLootable(o.Id) || !o.HasPosition) continue;
            lock (_gate) { if (!_lootAsked.Add(o.Id)) continue; }

            float reach = o.Radius > 0 ? Math.Max(o.Radius, 100f) : T.LootRange;
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
              + (T.AutoUndock ? $", {_launchAsks} launch ask(s)" : ", auto undock OFF")
            : _world.Anchored
                ? $"anchored to #{_world.AnchoredTo:X8} — riding, {_unanchorAsks} launch ask(s)"
                : "flying")}");
        lines.Add($"deaths         {Deaths}, {RepairsBought} repair(s) bought");

        string speedSource = T.TopSpeedOverride > 0f ? "set by hand"
                           : _world.ShipStat(ObjectStat.Speed) is > 0 ? "ship stat"
                           : _observedTopSpeed > T.FallbackSpeed ? "watched your throttle"
                           : "fallback, nothing published";
        string boostSource = T.BoostSpeedOverride > 0f ? "set by hand"
                           : _world.ShipStat(ObjectStat.BoostSpeed) is > 0 ? "ship stat"
                           : "never published";
        lines.Add($"throttle       {TopSpeed:F0}u/s ({speedSource})");
        lines.Add($"boost          " + (BoostSpeed > 0f
            ? $"{BoostSpeed:F0}u/s ({boostSource})"
              + (T.UseBoost
                    ? $", engaged past {BoostRunway(T.AsteroidStandoff):F0}u on a rock"
                      + $" ({Math.Clamp(BoostSpeed * T.BrakingSeconds, T.MinBrakeDistance, T.BrakingDistance):F0}u to brake"
                      + $" + {BoostSpeed * T.BoostShedSeconds:F0}u to shed it)"
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
        lines.Add($"combat reach   {(guns.Count == 0 ? "no weapon known" : $"{EffectiveRange(guns):F0}u, sit at {PreferredRange(guns, T.CloseInFactor):F0}u + target size")}");

        // Whether this hull can fight and mine at once, which is the whole line-ship question.
        var mineIds = Weapons.For(WeaponRole.Mining).Select(w => w.AbilityId).ToHashSet();
        var spare = guns.Where(w => !mineIds.Contains(w.AbilityId)).ToList();
        lines.Add($"return fire    " + (!T.FightWhileMining ? "off — a threat takes the whole ship"
            : spare.Count == 0
                ? "no gun that isn't a mining gun — a threat takes the whole ship"
                : $"{spare.Count} gun(s) free of the mining set, reach {EffectiveRange(spare):F0}u"
                  + $", {ReturnFireShots} shot(s) taken without leaving the rock"));
        lines.Add($"hold off       asteroid {T.AsteroidStandoff:F0}u, planetoid {T.PlanetoidStandoff:F0}u");

        if (T.AvoidCollisions)
        {
            var ahead = BlockerAhead(_world.MyVelocity, TopSpeed * T.CollisionLookaheadSeconds,
                                     CurrentTarget, DateTime.UtcNow, out float room);
            lines.Add($"clearance      asteroid r×{AsteroidColliderFactor:F2} +{T.AsteroidCollisionMargin:F0}u, "
                    + $"planetoid r×{T.PlanetoidClearanceFactor:F2} +{T.PlanetoidCollisionMargin:F0}u, "
                    + $"other r +{SafetyMargin:F0}u (hull {MyRadius:F0}u)");

            // Whether the sizes those clearances are built on are real or assumed. An assumed one
            // is not a problem in itself -- it is deliberately large -- but it is worth seeing,
            // because it means the server never described a body the ship is steering around.
            var unsized = _world.Snapshot()
                .Where(o => o.HasPosition && o.Radius <= 0 && EntityTypes.IsSolid(o.Id))
                .ToList();
            if (unsized.Count > 0)
                lines.Add($"assumed size   {unsized.Count} solid body(s) with no radius from the "
                        + $"server — planetoid {T.PlanetoidAssumedRadius:F0}u, asteroid "
                        + $"{T.AsteroidAssumedRadius:F0}u assumed, WhoIs asked");
            lines.Add($"collisions     avoiding, radius +{T.CollisionMargin:F0}u, looking "
                    + $"{TopSpeed * T.CollisionLookaheadSeconds:F0}u ahead — "
                    + (_world.MyVelocity.LengthSquared() < 1f ? "not moving"
                       : ahead is null ? "path clear"
                       : $"{ahead} at {room:F0}u")
                    + $", {NearMisses} avoided");

            // What the ship is choosing to hit, and whether it can yet tell. Both halves matter:
            // the threshold does nothing until enough rocks have been measured to convert a
            // radius into hull points.
            if (T.IgnoreCollisionHullFraction > 0f)
            {
                float maxHull = _world.MyMaxHull ?? 0f;
                int samples;
                lock (_gate) samples = _rockHpPerRadius.Count;

                lines.Add($"clip instead   under {T.IgnoreCollisionHullFraction:P0} of hull"
                        + (maxHull > 0 ? $" (~{maxHull * T.IgnoreCollisionHullFraction:F0} points)" : "")
                        + $" — {ClipsAllowed} flown through, "
                        + (samples < RockSizeSamplesNeeded
                           ? $"rock sizes not learned yet ({samples}/{RockSizeSamplesNeeded} measured, dodging all)"
                           : $"{samples} rock(s) measured")
                        + (TurnSpeed is { } deg ? $", turn {deg:F0}°/s" : ", turn rate unknown — braking always"));
            }
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
                    + (T.FireGunsWhileMining ? "" : "  (guns-on-rocks off)"));
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
                : T.ScanOnlyWhenFiltering && !Filtering
                    ? "idle (no resource filter set)"
                    : confirmed >= T.ScanQueueDepth
                        ? $"idle (queue full — {confirmed} confirmed, wants {T.ScanQueueDepth})"
                        : CanAffordScan(scanner) ? "ready" : "waiting for power";
            string kind = scanner.Area switch
            {
                true => "area",
                false => "single-target",
                null => "area unknown — scan once by hand to settle it",
            };
            lines.Add($"scanner        ability #{scanner.AbilityId}, {kind}, reach "
                    + $"{scanner.MaxRange ?? T.FallbackRange:F0}u, "
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
                + $"stops scanning at {T.ScanQueueDepth}, scans trusted {T.ScanFreshnessSeconds}s");

        AddMeterLines(lines, mineGuns, nowD);

        var repairs = Weapons.For(WeaponRole.Repair);
        lines.Add($"repair         {(repairs.Count == 0
            ? "none known — cast Damage Control once by hand to teach it"
            : $"{string.Join(", ", repairs.Select(w => w.Label))} below {T.RepairAtHull:P0} hull"
              + $" ({RepairsCast} cast)")}"
                + (T.UseRepairAbility ? "" : "  (off)"));

        var stations = HostileStations();
        string nearestStation = stations
            .OrderBy(s => _world.DistanceToMe(s) ?? float.MaxValue)
            .Select(s => $"{s} at {_world.DistanceToMe(s) ?? 0f:F0}u")
            .FirstOrDefault() ?? "none located";
        lines.Add($"enemy stations {(T.AvoidHostileStations
            ? $"avoiding within {T.HostileStationKeepOut:F0}u — {nearestStation}"
            : "not avoided (off)")}");
        lines.Add($"firing         {(T.HoldFireUntilOptimal
            ? "holds each weapon for its optimal range while closing"
            : "opens up at max range")}");
        lines.Add($"hunting        {(T.Prey.Count == 0 ? "any NPC" : string.Join(", ", T.Prey))}"
                + (T.AttackPlayers ? " + players" : ""));
        lines.Add($"mining for     {(!Filtering
            ? "any resource"
            : string.Join(" > ", T.WantedResources) + "   (best first)")}");

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
                    + $"holding {(chased is not null ? FollowStandoff(chased) : T.FollowDistance):F0}u"
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

        if (!T.FetchCatalogue) lines.Add("               (requests off — passive sniffing only)");

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
