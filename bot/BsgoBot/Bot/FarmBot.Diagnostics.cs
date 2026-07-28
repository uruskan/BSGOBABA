using System.Numerics;
using BsgoBot.Cards;
using BsgoBot.Net;
using BsgoBot.Protocol;
using BsgoBot.Proxy;
using BsgoBot.World;

namespace BsgoBot.Bot;

/// <summary>How a diagnostics value should read: the UI maps these to theme colours.</summary>
public enum DiagTone { Normal, Muted, Good, Warn, Bad, Accent }

/// <summary>One diagnostics fact. An empty label means a full-width prose row.</summary>
public sealed record DiagRow(string Label, string Value, DiagTone Tone = DiagTone.Normal);

/// <summary>A titled group of diagnostics rows, so the panel reads as cards, not a scroll.</summary>
public sealed record DiagSection(string Title, List<DiagRow> Rows);

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
    /// Answers, in plain numbers, the two questions the status line can only hint at: what does
    /// the bot actually know, and how far away is everything. Grouped into titled sections so
    /// the panel can lay them out as cards rather than one clipped scroll of text.
    /// </summary>
    public List<DiagSection> DiagnosticSections()
    {
        var objs = _world.Snapshot();
        var nowD = DateTime.UtcNow;
        var sections = new List<DiagSection>();
        var rows = new List<DiagRow>();
        void Sec(string title) => sections.Add(new DiagSection(title, rows = []));
        void Row(string label, string value, DiagTone tone = DiagTone.Normal)
            => rows.Add(new DiagRow(label, value, tone));

        // ---------------- ship
        Sec("Ship");
        Row("player id", _world.MyPlayerId == 0 ? "unknown" : _world.MyPlayerId.ToString(),
            _world.MyPlayerId == 0 ? DiagTone.Muted : DiagTone.Normal);
        Row("ship", _world.MyObjectId == 0 ? "unknown"
            : $"#{_world.MyObjectId:X8} {_world.MyFaction}/{_world.MyGroup}");
        Row("position", _world.MyPositionKnown
            ? $"{_world.MyPosition.X:F0}, {_world.MyPosition.Y:F0}, {_world.MyPosition.Z:F0}"
            : "unknown");
        Row("hull", Points(_world.MyHull, _world.MyMaxHull, _world.MyHullFraction),
            _world.MyHullFraction is < 0.5f ? DiagTone.Warn : DiagTone.Normal);
        Row("power", Points(_world.MyPower, _world.MyMaxPower, _world.MyPowerFraction));
        Row("condition", Condition is { } cond
            ? $"{cond.Now:F0} / {cond.Max:F0} ({cond.Now / cond.Max:P0})"
            : _world.MyCondition is { } bare ? $"{bare:F0} (no ship card yet)" : "unknown");
        Row("hangar", _hangarSince is { } inHangar
            ? $"out of sector for {(nowD - inHangar).TotalSeconds:F0}s"
              + (T.AutoUndock ? $", {_launchAsks} launch ask(s)" : ", auto undock OFF")
            : _world.Anchored
                ? $"anchored to #{_world.AnchoredTo:X8} — riding, {_unanchorAsks} launch ask(s)"
                : "flying");
        Row("deaths", $"{Deaths}, {RepairsBought} repair(s) bought",
            Deaths > 0 ? DiagTone.Warn : DiagTone.Normal);

        // ---------------- flying
        Sec("Flying");
        string speedSource = T.TopSpeedOverride > 0f ? "set by hand"
                           : _world.ShipStat(ObjectStat.Speed) is > 0 ? "ship stat"
                           : _observedTopSpeed > T.FallbackSpeed ? "watched your throttle"
                           : "fallback, nothing published";
        string boostSource = T.BoostSpeedOverride > 0f ? "set by hand"
                           : _world.ShipStat(ObjectStat.BoostSpeed) is > 0 ? "ship stat"
                           : "never published";
        Row("throttle", $"{TopSpeed:F0}u/s ({speedSource})");
        Row("boost", BoostSpeed > 0f
            ? $"{BoostSpeed:F0}u/s ({boostSource})"
              + (T.UseBoost
                    ? $", engaged past {BoostRunway(T.AsteroidStandoff):F0}u on a rock"
                      + $" ({Math.Clamp(BoostSpeed * T.BrakingSeconds, T.MinBrakeDistance, T.BrakingDistance):F0}u to brake"
                      + $" + {BoostSpeed * T.BoostShedSeconds:F0}u to shed it)"
                    : ", toggle is OFF")
            : $"unusable — no BoostSpeed ({boostSource}), so the gear is never engaged",
            BoostSpeed > 0f ? DiagTone.Normal : DiagTone.Muted);
        // The EFFECTIVE speed, not the stored throttle. In boost gear the throttle number does
        // nothing — printing it read "52u/s in Boost" while the ship was genuinely doing 86.
        Row("flying", _throttleOpen
            ? $"{SpeedInGear(_gear):F0}u/s in {_gear}"
              + (_gear == Gear.Boost ? $" ({_throttle:F0}u/s stored for Regular)" : "")
            : "stopped");
        // Where the ship IS, as opposed to where dead reckoning has got to. Every distance the
        // bot acts on is measured from this, so its age is the error bar on all of them.
        double fixAge = _world.MyFixAgeSeconds;
        Row("position fix", double.IsPositiveInfinity(fixAge) || fixAge > 1e6
            ? "never stated by the server — everything is dead reckoning"
            : $"{fixAge:F1}s old"
              + (SelfPositionSuspect ? ", FLOWN SINCE — distances unproven" : ", trusted")
              + $", {PositionResyncs} stop(s) to re-confirm",
            SelfPositionSuspect ? DiagTone.Warn : DiagTone.Normal);

        // ---------------- combat
        var guns = Weapons.For(WeaponRole.Combat);
        var (mineGuns, improvised) = MiningWeapons();
        Sec("Combat");
        Row("combat reach", guns.Count == 0 ? "no weapon known"
            : $"{EffectiveRange(guns):F0}u, sit at {PreferredRange(guns, T.CloseInFactor):F0}u + target size");

        // Whether this hull can fight and mine at once, which is the whole line-ship question.
        var mineIds = Weapons.For(WeaponRole.Mining).Select(w => w.AbilityId).ToHashSet();
        var spare = guns.Where(w => !mineIds.Contains(w.AbilityId)).ToList();
        Row("return fire", !T.FightWhileMining ? "off — a threat takes the whole ship"
            : spare.Count == 0
                ? "no gun that isn't a mining gun — a threat takes the whole ship"
                : $"{spare.Count} gun(s) free of the mining set, reach {EffectiveRange(spare):F0}u"
                  + $", {ReturnFireShots} shot(s) taken without leaving the rock");
        Row("firing", T.HoldFireUntilOptimal
            ? "holds each weapon for its optimal range while closing"
            : "opens up at max range");
        Row("hunting", (T.Prey.Count == 0 ? "any NPC" : string.Join(", ", T.Prey))
            + (T.AttackPlayers ? " + players" : ""));
        var stations = HostileStations();
        string nearestStation = stations
            .OrderBy(s2 => _world.DistanceToMe(s2) ?? float.MaxValue)
            .Select(s2 => $"{s2} at {_world.DistanceToMe(s2) ?? 0f:F0}u")
            .FirstOrDefault() ?? "none located";
        Row("enemy stations", T.AvoidHostileStations
            ? $"avoiding within {T.HostileStationKeepOut:F0}u — {nearestStation}"
            : "not avoided (off)",
            T.AvoidHostileStations ? DiagTone.Normal : DiagTone.Muted);
        if (_pinned != 0)
            Row("pinned", $"#{_pinned:X8} — held ahead of the hunting rules", DiagTone.Accent);
        if (_following)
        {
            var chased = _world.Get(_followTarget);
            Row("flying to", $"{chased?.ToString() ?? $"#{_followTarget:X8}"} at "
                + $"{(chased is not null ? _world.DistanceToMe(chased) ?? 0f : 0f):F0}u, "
                + $"holding {(chased is not null ? FollowStandoff(chased) : T.FollowDistance):F0}u"
                + (_followHold ? _followLosingGround ? " — losing ground, still chasing" : " — keeping station"
                               : " — stops on arrival"), DiagTone.Accent);
        }

        // ---------------- avoidance
        Sec("Avoidance");
        Row("hold off", $"asteroid {T.AsteroidStandoff:F0}u, planetoid {T.PlanetoidStandoff:F0}u");
        if (T.AvoidCollisions)
        {
            var ahead = BlockerAhead(_world.MyVelocity, TopSpeed * T.CollisionLookaheadSeconds,
                                     CurrentTarget, nowD, out float room);
            Row("clearance", $"asteroid r×{AsteroidColliderFactor:F2} +{T.AsteroidCollisionMargin:F0}u, "
                + $"planetoid fixed 900u sphere +{T.PlanetoidCollisionMargin:F0}u, "
                + $"other r +{SafetyMargin:F0}u");

            // Where our own size came from. Worth a row of its own because everything above is
            // built on it and the server never states it — a wrong value here is invisible
            // otherwise, and looks like bad flying rather than a bad number.
            Row("my hull", $"{MyRadius:F0}u half-size — {HullRadiusSource}");

            // Whether the sizes those clearances are built on are real or assumed. An assumed one
            // is not a problem in itself -- it is deliberately large -- but it is worth seeing,
            // because it means the server never described a body the ship is steering around.
            var unsized = objs
                .Where(o => o.HasPosition && o.Radius <= 0 && EntityTypes.IsSolid(o.Id))
                .ToList();
            if (unsized.Count > 0)
                Row("assumed size", $"{unsized.Count} solid body(s) with no radius from the "
                    + $"server — planet {T.PlanetoidAssumedRadius:F0}u, asteroid "
                    + $"{T.AsteroidAssumedRadius:F0}u assumed, WhoIs asked", DiagTone.Muted);
            Row("collisions", $"avoiding, radius +{T.CollisionMargin:F0}u, looking "
                + $"{TopSpeed * T.CollisionLookaheadSeconds:F0}u ahead — "
                + (_world.MyVelocity.LengthSquared() < 1f ? "not moving"
                   : ahead is null ? "path clear"
                   : $"{ahead} at {room:F0}u")
                + $", {NearMisses} avoided",
                ahead is null ? DiagTone.Normal : DiagTone.Warn);

            // What the ship is choosing to hit, and whether it can yet tell. Both halves matter:
            // the threshold does nothing until enough rocks have been measured to convert a
            // radius into hull points.
            if (T.IgnoreCollisionHullFraction > 0f)
            {
                float maxHull = _world.MyMaxHull ?? 0f;
                int samples;
                lock (_gate) samples = _rockHpPerRadius.Count;

                Row("clip instead", $"under {T.IgnoreCollisionHullFraction:P0} of hull"
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
            Row("collisions", $"NOT avoided (off) — {NearMisses} avoided before it was turned off",
                DiagTone.Warn);
        }

        // ---------------- mining
        Sec("Mining");
        Row("mining reach", (mineGuns.Count == 0 ? "no weapon known" : $"{EffectiveRange(mineGuns):F0}u")
            + (improvised && mineGuns.Count > 0 ? " (no laser — using your guns)" : ""));
        if (mineGuns.Count > 0)
        {
            var mineFire = MiningFireSet(mineGuns, improvised);
            Row("mining fires", string.Join(", ", mineFire.Select(w => $"{w.Label} {w.Role}"))
                + (T.FireGunsWhileMining ? "" : "  (guns-on-rocks off)"));
        }

        var scanner = Scanner();
        int unknown = objs.Count(o => NeedsScan(o, nowD));
        int known = objs.Count(o => EntityTypes.IsMinable(o.Id) && KnownContents(o, nowD));
        if (scanner is not null)
        {
            // Counted once. Each call is a full sweep of the sector, and this line used to make
            // two of them to print one number.
            int confirmed = ConfirmedRocks(nowD);
            bool dead = !ScannerAnswering;
            string gate = dead
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
            Row("scanner", $"ability #{scanner.AbilityId}, {kind}, reach "
                + $"{scanner.MaxRange ?? T.FallbackRange:F0}u, "
                + $"costs {scanner.PowerCost ?? 0f:F0} power — {gate}",
                dead ? DiagTone.Bad : DiagTone.Normal);
        }
        else
        {
            int left = Weapons.ProbeCandidates().Count(w => !IsProbed(w.AbilityId));
            Row("scanner", $"not found — {left} ability(s) left to test"
                + (left == 0 ? " (none of yours answered a scan)" : ""), DiagTone.Warn);
        }
        Row("rock contents", $"{known} known, {unknown} unknown ({ScansSent} scans sent)");
        Row("mining queue", $"{ConfirmedRocks(nowD)} confirmed and worth mining, "
            + $"stops scanning at {T.ScanQueueDepth}, scans trusted {T.ScanFreshnessSeconds}s");
        Row("mining for", !Filtering
            ? "any resource"
            : string.Join(" > ", T.WantedResources) + "   (best first)");

        // ---------------- meter
        sections.Add(MeterSection(mineGuns, nowD));

        // ---------------- loadout
        Sec("Loadout");
        var repairs = Weapons.For(WeaponRole.Repair);
        Row("repair", (repairs.Count == 0
            ? "none known — cast Damage Control once by hand to teach it"
            : $"{string.Join(", ", repairs.Select(w => w.Label))} below {T.RepairAtHull:P0} hull"
              + $" ({RepairsCast} cast)")
            + (T.UseRepairAbility ? "" : "  (off)"));

        // What the server said is bolted to the ship, against what you told the bot it is.
        var slots = _world.MySlots();
        int declared = Weapons.All().Count(w => w.RoleFromUser || w.Name.Length > 0);
        Row("slots", (slots.Count == 0
            ? "no slot list from this server"
            : $"{slots.Count(s => s.Filled)} of {slots.Count} slots filled")
            + $", {declared} declared by you");
        foreach (var w in Weapons.All()) Row("", w.Describe(), DiagTone.Muted);
        if (Weapons.Count == 0) Row("", "(nothing learned yet)", DiagTone.Muted);

        // ---------------- sector
        Sec("Sector");
        NearestRow(Row, "nearest hostile", objs.Where(CombatCandidate));
        NearestRow(Row, "nearest rock", objs.Where(MiningCandidate));
        int unlocated = objs.Count(o => !o.HasPosition);
        Row("objects", $"{objs.Count} known, {unlocated} without a position");

        // What your own client is filtering out, using its own DradisHelper rule.
        var det = _world.Detection;
        if (det.Known)
        {
            var bands = objs.Where(o => !o.IsMe && o.HasPosition)
                            .GroupBy(o => _world.LayerOf(o, det))
                            .ToDictionary(gr => gr.Key, gr => gr.Count());
            Row("detection", $"dradis {det.Dradis:F0}u, map {det.Map:F0}u, visual {det.Visual:F0}u");
            Row("bands", $"visual {bands.GetValueOrDefault(ContactLayer.Visual)}"
                + $", dradis {bands.GetValueOrDefault(ContactLayer.Dradis)}"
                + $", map {bands.GetValueOrDefault(ContactLayer.Map)}"
                + $", dark {bands.GetValueOrDefault(ContactLayer.Dark)}");
        }
        else
        {
            Row("detection", "radii not published by this server", DiagTone.Muted);
        }

        // ---------------- catalogue
        sections.Add(CatalogueSection(objs));

        // ---------------- fights
        Sec("Fights");
        foreach (var line in Fights.Describe()) Row("", line, DiagTone.Muted);
        var fought = Fights.Classes();
        if (fought.Count > 0)
        {
            Row("", "fought:", DiagTone.Normal);
            foreach (var line in Fights.DescribeClasses()) Row("", line, DiagTone.Muted);
        }

        return sections;
    }

    /// <summary>
    /// The measured half of the diagnostics: what the ship actually earns, as opposed to what
    /// the item cards say it should. Every row here is silent until it has real data behind it,
    /// because a made-up number is worse than a missing one.
    /// </summary>
    private DiagSection MeterSection(List<Weapon> mineGuns, DateTime now)
    {
        var rows = new List<DiagRow>();
        void Row(string label, string value, DiagTone tone = DiagTone.Normal)
            => rows.Add(new DiagRow(label, value, tone));

        string regen = Meter.Regen is { } r
            ? $"{r:F2}/sec measured over {Meter.RegenSampleSeconds:F0}s of quiet"
            : $"measuring… ({Meter.RegenSampleSeconds:F0}s so far — needs the guns off)";
        Row("power regen", regen);

        var cap = Meter.Capacity(mineGuns, _world);
        if (cap is not null)
        {
            Row("mining draw", $"{cap.Guns} gun(s), {cap.DrawPerSecond:F1} power/sec, "
                + $"{cap.RawDamagePerSecond:F1} dmg/sec if power were free");
            if (cap.SustainedDamagePerSecond is { } sus)
            {
                string verdict = cap.PowerLimited
                    ? $"POWER-LIMITED — recharge feeds {cap.SustainableGuns:F1} of {cap.Guns} gun(s)"
                    : "not power-limited — the guns are the ceiling";
                Row("mining rate", $"{sus:F1} dmg/sec sustained "
                    + $"({cap.DamagePerPower:F2} dmg per power) — {verdict}",
                    cap.PowerLimited ? DiagTone.Warn : DiagTone.Good);
            }
        }
        else if (mineGuns.Count > 0)
        {
            Row("mining draw", "slot stats haven't published cost/cooldown yet", DiagTone.Muted);
        }

        double tracked = Meter.TotalTrackedSeconds;
        if (tracked > 5)
            Row("time split", $"{Meter.FractionIn(MiningActivity.Firing):P0} firing, "
                + $"{Meter.FractionIn(MiningActivity.Travelling):P0} travelling, "
                + $"{Meter.FractionIn(MiningActivity.Holding):P0} holding, "
                + $"{Meter.FractionIn(MiningActivity.Idle):P0} idle "
                + $"(over {tracked / 60.0:F1} min)");

        long total = Meter.TotalGained;
        if (total > 0)
        {
            var span = Meter.Elapsed(now);
            string ore = Meter.MinedPerHour(now) is { } mph ? $"{mph:F0} ore/hour" : "…";
            Row("mined", $"{Meter.MinedGained:N0} units in {span.TotalMinutes:F1} min = {ore}",
                DiagTone.Accent);
            if (total != Meter.MinedGained)
                Row("banked", $"{total:N0} units total (includes loot and non-ore)");
            foreach (var (guid, count) in Meter.AllGained().Take(6))
                Row(NameItem(guid), $"{count:N0}", DiagTone.Muted);
        }
        else
        {
            Row("mined", "nothing yet — waiting on the first hold gain from the server",
                DiagTone.Muted);
        }

        return new DiagSection("Meter", rows);
    }

    /// <summary>
    /// What the server's own catalogue has told us, and what it says about what is in front of
    /// us right now.
    ///
    /// The per-contact block is the one to read: it is the difference between "an enemy" and
    /// "a tier 3 gunship with 4,100 hull and 210 avoidance", which is the whole reason for
    /// reading cards rather than inferring from damage taken.
    /// </summary>
    private DiagSection CatalogueSection(List<SpaceObj> objs)
    {
        var rows = new List<DiagRow>();
        foreach (var line in Cards.Describe()) rows.Add(new DiagRow("", line, DiagTone.Muted));
        if (!T.FetchCatalogue)
            rows.Add(new DiagRow("", "(requests off — passive sniffing only)", DiagTone.Muted));

        // Only hostiles, and only ones we can actually see: the point is the fight in front of
        // us, not a dump of everything ever cached.
        var seen = objs.Where(o => !o.IsMe && o.CardGuid != 0 && o.HasPosition && IsHostile(o))
                       .GroupBy(o => o.CardGuid)
                       .OrderBy(g => _world.DistanceToMe(g.First()) ?? float.MaxValue)
                       .Take(8)
                       .ToList();

        foreach (var group in seen)
        {
            var ship = Cards.Ship(group.Key);
            var world = Cards.World(group.Key);

            string name = world?.PrefabName is { Length: > 0 } p ? p : $"card {group.Key}";
            string count = group.Count() > 1 ? $" x{group.Count()}" : "";

            if (ship is null)
            {
                rows.Add(new DiagRow(name + count, "card not fetched yet", DiagTone.Muted));
                continue;
            }

            var guns = Cards.WeaponsOf(group.Key);
            string arms = guns.Count == 0
                ? "armament not resolved yet"
                : $"{guns.Count} weapon(s), "
                + $"{guns.Sum(g => g.Dps ?? 0f):F0} dps, reach {guns.Max(g => g.MaxRange ?? 0f):F0}u";

            rows.Add(new DiagRow($"T{ship.Tier} {name}{count}",
                $"hull {ship.MaxHull?.ToString("F0") ?? "?"}"
                + $", avoid {ship.Avoidance?.ToString("F0") ?? "?"}"
                + $", armor {ship.Armor?.ToString("F0") ?? "?"}"
                + $", {ship.RoleText} — {arms}"));
        }

        return new DiagSection("Catalogue", rows);
    }

    private void NearestRow(Action<string, string, DiagTone> row, string label,
                            IEnumerable<SpaceObj> pool)
    {
        var located = pool.Where(o => o.HasPosition).ToList();
        if (located.Count == 0) { row(label, "none located", DiagTone.Muted); return; }

        var now = DateTime.UtcNow;
        var best = located.OrderBy(o => Vector3.Distance(o.PredictedPosition(now), _world.MyPosition)).First();
        string extra = best.Scanned ? $"  {NameItem(best.ResourceGuid)} x{best.ResourceCount}"
                     : best.StatsKnown ? $"  hull {best.Hull:F0}"
                     : "";
        row(label, $"{best} at {_world.DistanceToMe(best):F0}u{extra}", DiagTone.Normal);
    }

    /// <summary>"430 / 495 (87%)", degrading to bare points until the server publishes a maximum.</summary>
    private static string Points(float value, float? max, float? fraction) =>
        max is > 0 && fraction is not null
            ? $"{value:F0} / {max.Value:F0} ({fraction.Value:P0})"
            : $"{value:F0} points (maximum not published)";

    /// <summary>
    /// Best available name for an item guid: the client's own resource names first, then the
    /// server catalogue, then the bare number. This is what turns "resource 256371252" — an
    /// unreadable guid in the old panel — into whatever the card actually calls it.
    /// </summary>
    public string NameItem(uint guid)
    {
        if (guid == 0) return "?";
        if (Enum.IsDefined(typeof(ResourceType), guid)) return ((ResourceType)guid).ToString();
        var card = Cards.World(guid);
        if (card?.PrefabName is { Length: > 0 } name) return name;
        return $"item {guid}";
    }
}
