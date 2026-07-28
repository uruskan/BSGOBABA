using BsgoBot.Protocol;
using BsgoBot.World;

namespace BsgoBot.Bot;

/// <summary>What the ship is doing right now, for the time-split accounting.</summary>
public enum MiningActivity
{
    Idle = 0,
    /// <summary>Flying to a rock. The tax that card stats never show.</summary>
    Travelling = 1,
    /// <summary>In range with at least one mining weapon actually firing.</summary>
    Firing = 2,
    /// <summary>In range but holding — cooldown, waiting for a scan, or out of power.</summary>
    Holding = 3,
}

/// <summary>
/// Measures what the ship really earns, rather than what the item cards imply.
///
/// Three numbers no store card can give you:
///
///  1. **Power regen**, in points per second. The server never sends it as a rate — only as
///     updated PowerPoints values — so the only way to know it is to watch the pool climb while
///     nothing is spending. That single number decides every mining fit, because a power-limited
///     ship mines at <c>regen × damage-per-power</c> and nothing else.
///
///  2. **Ore per hour**, from <c>PlayerProtocol Reply.HoldItems</c> — the server stating what
///     actually reached the hold. Not inferred from scans (which say what a rock holds, not what
///     we extracted) and not from kills (another player can break the rock we were shooting).
///
///  3. **The travel split**. Damage-at-the-rock is not ore-per-hour: a hull with twice the
///     throughput and half the speed can easily come out behind. Only measurement settles it,
///     and the bot is the only thing that knows which state it was in.
/// </summary>
public sealed class MiningMeter
{
    private readonly Lock _gate = new();

    // ---- power regen ---------------------------------------------------------------
    private float _lastPower;
    private DateTime _lastPowerAt = DateTime.MinValue;
    private double _regenPoints;
    private double _regenSeconds;
    private float _peakRegen;

    // ---- yield ---------------------------------------------------------------------
    private readonly Dictionary<uint, long> _gained = new();
    private DateTime _startedAt = DateTime.MinValue;

    // ---- time split ----------------------------------------------------------------
    private readonly Dictionary<MiningActivity, double> _spent = new();
    private MiningActivity _activity = MiningActivity.Idle;
    private DateTime _activitySince = DateTime.MinValue;

    /// <summary>
    /// Ignore a power sample taken within this long of firing. A toggle weapon's draw and the
    /// server's power push are not synchronised, so a sample straight after a shot can show the
    /// pool rising while a cannon is mid-cycle and quietly understate the cost.
    /// </summary>
    public double FiringGuardSeconds { get; set; } = 1.5;

    /// <summary>Longest gap between two power updates still treated as one continuous interval.
    /// A larger gap means we missed pushes and the average would be meaningless.</summary>
    public double MaxSampleGapSeconds { get; set; } = 6.0;

    // ------------------------------------------------------------------ regen

    /// <summary>
    /// Folds in a power reading. Only rising, non-clipped intervals with no recent firing count,
    /// which is what makes the result the ship's true passive recharge.
    /// </summary>
    public void OnPower(float power, float? maxPower, DateTime now, DateTime lastFired)
    {
        lock (_gate)
        {
            if (_lastPowerAt == DateTime.MinValue)
            {
                _lastPower = power;
                _lastPowerAt = now;
                return;
            }

            // An unchanged reading is not a measurement. The server publishes PowerPoints only
            // when the value moves, while this is sampled every tick — so a pool climbing 6.24
            // points once a second is seen as three identical readings and then a jump.
            //
            // The baseline used to advance on every one of those, so the whole second's climb was
            // divided by the 250ms since the last SAMPLE rather than the time since the last
            // CHANGE. That reports the rate multiplied by the ratio between the two, which is how
            // a hull that regenerates 6.24/s measured a little over 30.
            if (Math.Abs(power - _lastPower) < 0.001f) return;

            float prev = _lastPower;
            var prevAt = _lastPowerAt;
            _lastPower = power;
            _lastPowerAt = now;

            double dt = (now - prevAt).TotalSeconds;
            if (dt <= 0.05 || dt > MaxSampleGapSeconds) return;

            // Anything fired recently and this interval is contaminated.
            if (lastFired != DateTime.MinValue
                && (now - lastFired).TotalSeconds < FiringGuardSeconds) return;

            float delta = power - prev;
            if (delta <= 0f) return;                       // spending, or flat

            // A pool that hit its ceiling mid-interval regenerated for only part of it, so the
            // rate would come out low. Drop it.
            if (maxPower is { } max && max > 0f && power >= max - 0.01f) return;

            _regenPoints += delta;
            _regenSeconds += dt;

            float rate = (float)(delta / dt);
            if (rate > _peakRegen) _peakRegen = rate;
        }
    }

    /// <summary>Measured passive recharge in points per second, or null until enough samples.</summary>
    public float? Regen
    {
        get
        {
            lock (_gate)
                return _regenSeconds >= 3.0 ? (float)(_regenPoints / _regenSeconds) : null;
        }
    }

    /// <summary>Seconds of clean climbing observed, so the UI can say how solid the figure is.</summary>
    public double RegenSampleSeconds { get { lock (_gate) return _regenSeconds; } }

    // ------------------------------------------------------------------ yield

    /// <summary>Records what the server just put in the hold.</summary>
    public void OnHoldGained(IReadOnlyList<LootItem> items, DateTime now)
    {
        lock (_gate)
        {
            if (_startedAt == DateTime.MinValue) _startedAt = now;
            foreach (var it in items)
            {
                if (it.Count == 0) continue;
                _gained[it.CardGuid] = _gained.GetValueOrDefault(it.CardGuid) + it.Count;
            }
        }
    }

    /// <summary>Total units of one resource banked since the meter started.</summary>
    public long Gained(ResourceType resource)
    {
        lock (_gate) return _gained.GetValueOrDefault((uint)resource);
    }

    /// <summary>Every resource banked, richest first.</summary>
    public List<(uint Guid, long Count)> AllGained()
    {
        lock (_gate)
            return _gained.Where(kv => kv.Value > 0)
                          .OrderByDescending(kv => kv.Value)
                          .Select(kv => (kv.Key, kv.Value))
                          .ToList();
    }

    public long TotalGained { get { lock (_gate) return _gained.Values.Sum(); } }

    /// <summary>The three things you can actually shoot out of a rock.</summary>
    private static readonly uint[] Minables =
    [
        (uint)ResourceType.Tylium, (uint)ResourceType.Titanium, (uint)ResourceType.Water,
    ];

    /// <summary>
    /// Ore only. <c>HoldItems</c> fires for anything that reaches the hold — loot from a wreck,
    /// a store purchase, an assignment reward — so a raw total is earnings, not mining rate.
    /// Comparing two hulls needs the mined part alone.
    /// </summary>
    public long MinedGained
    {
        get { lock (_gate) return Minables.Sum(g => _gained.GetValueOrDefault(g)); }
    }

    /// <summary>Ore per hour — the number that settles a hull argument, because the travel time
    /// is already inside it.</summary>
    public double? MinedPerHour(DateTime now)
    {
        double hours = Elapsed(now).TotalHours;
        if (hours < 1.0 / 3600.0) return null;
        return MinedGained / hours;
    }

    /// <summary>How long the meter has been running, from the first thing it banked.</summary>
    public TimeSpan Elapsed(DateTime now)
    {
        lock (_gate)
            return _startedAt == DateTime.MinValue ? TimeSpan.Zero : now - _startedAt;
    }

    /// <summary>Units per hour across everything banked. The number that actually settles a
    /// hull argument, because it already contains the travel time.</summary>
    public double? UnitsPerHour(DateTime now)
    {
        double hours = Elapsed(now).TotalHours;
        if (hours < 1.0 / 3600.0) return null;
        return TotalGained / hours;
    }

    // ------------------------------------------------------------------ time split

    /// <summary>Notes what the ship is doing. Cheap enough to call every tick.</summary>
    public void Note(MiningActivity activity, DateTime now)
    {
        lock (_gate)
        {
            if (_activitySince == DateTime.MinValue)
            {
                _activity = activity;
                _activitySince = now;
                return;
            }

            double dt = (now - _activitySince).TotalSeconds;
            // A tick gap this large means the bot was stopped, not that we spent the time here.
            if (dt > 0 && dt < 30.0)
                _spent[_activity] = _spent.GetValueOrDefault(_activity) + dt;

            _activity = activity;
            _activitySince = now;
        }
    }

    public double SecondsIn(MiningActivity a) { lock (_gate) return _spent.GetValueOrDefault(a); }

    public double TotalTrackedSeconds { get { lock (_gate) return _spent.Values.Sum(); } }

    /// <summary>Fraction of tracked time in one activity, 0 if nothing tracked yet.</summary>
    public double FractionIn(MiningActivity a)
    {
        double total = TotalTrackedSeconds;
        return total <= 0 ? 0 : SecondsIn(a) / total;
    }

    public void Reset()
    {
        lock (_gate)
        {
            _regenPoints = _regenSeconds = 0;
            _peakRegen = 0;
            _lastPowerAt = DateTime.MinValue;
            _gained.Clear();
            _startedAt = DateTime.MinValue;
            _spent.Clear();
            _activitySince = DateTime.MinValue;
            _activity = MiningActivity.Idle;
        }
    }

    /// <summary>
    /// What the fitted mining weapons could do if power were free, against what the measured
    /// regen actually sustains. The gap is the answer to "is my third cannon doing anything".
    ///
    /// Damage figures are the server's own per-slot stats, so this needs no card lookup — but it
    /// deliberately reports nothing rather than guessing when a slot published no cost.
    /// </summary>
    public MiningCapacity? Capacity(List<Weapon> guns, WorldState world)
    {
        var priced = guns
            .Where(w => w.PowerCost is > 0 && w.Cooldown is > 0)
            .ToList();
        if (priced.Count == 0) return null;

        double drawPerSecond = priced.Sum(w => w.PowerCost!.Value / w.Cooldown!.Value);

        // Mining damage per shot, from the slot stats. MiningDamageHigh/Low already carry the
        // real numbers; the x5 multiplier is applied server-side in DamageCalculator and is not
        // published, so this is raw damage — fine, because it only ever gets compared to itself.
        double rawPerSecond = 0;
        foreach (var w in priced)
        {
            float? low = world.SlotStat(w.AbilityId, ObjectStat.MiningDamageLow)
                      ?? world.SlotStat(w.AbilityId, ObjectStat.DamageLow);
            float? high = world.SlotStat(w.AbilityId, ObjectStat.MiningDamageHigh)
                       ?? world.SlotStat(w.AbilityId, ObjectStat.DamageHigh);
            if (low is null && high is null) continue;
            double avg = ((low ?? high)!.Value + (high ?? low)!.Value) / 2.0;
            rawPerSecond += avg / w.Cooldown!.Value;
        }

        if (rawPerSecond <= 0 || drawPerSecond <= 0) return null;

        double perPower = rawPerSecond / drawPerSecond;
        float? regen = Regen;

        return new MiningCapacity
        {
            Guns = priced.Count,
            DrawPerSecond = drawPerSecond,
            RawDamagePerSecond = rawPerSecond,
            DamagePerPower = perPower,
            Regen = regen,
            SustainedDamagePerSecond = regen is { } r ? Math.Min(rawPerSecond, r * perPower) : null,
        };
    }
}

/// <summary>The throughput picture for the mining weapons currently fitted.</summary>
public sealed class MiningCapacity
{
    public int Guns { get; init; }
    /// <summary>Power points per second all fitted mining weapons draw at full rate.</summary>
    public double DrawPerSecond { get; init; }
    /// <summary>Damage per second if power were free.</summary>
    public double RawDamagePerSecond { get; init; }
    public double DamagePerPower { get; init; }
    public float? Regen { get; init; }
    /// <summary>What the measured regen actually sustains, null until regen is known.</summary>
    public double? SustainedDamagePerSecond { get; init; }

    /// <summary>True when the guns can out-draw the recharge — i.e. adding another does nothing
    /// for the sustained rate.</summary>
    public bool PowerLimited => Regen is { } r && DrawPerSecond > r;

    /// <summary>How many of the fitted guns the recharge can actually feed.</summary>
    public double? SustainableGuns =>
        Regen is { } r && DrawPerSecond > 0 ? r / (DrawPerSecond / Math.Max(Guns, 1)) : null;
}
