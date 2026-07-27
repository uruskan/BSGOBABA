using BsgoBot.World;

namespace BsgoBot.Bot;

/// <summary>What we learned about one class of ship, keyed by its catalogue card guid.</summary>
public sealed class ClassRecord
{
    public uint CardGuid;
    public string Name = "";

    public float DamageDealt;
    public float DamageTaken;
    public int HitsDealt;
    public int HitsTaken;
    public int CritsDealt;
    public int CritsTaken;
    public int Killed;
    public int KilledUs;

    /// <summary>Total damage it took to kill one, averaged over confirmed kills. This is the
    /// number a time-to-kill estimate needs, and it is measured rather than assumed — armour and
    /// resistances sit between a card's hull figure and what a fight actually costs.</summary>
    public float DamagePerKill => Killed > 0 ? DamageDealt / Killed : 0f;

    public float MeanHitDealt => HitsDealt > 0 ? DamageDealt / HitsDealt : 0f;
    public float MeanHitTaken => HitsTaken > 0 ? DamageTaken / HitsTaken : 0f;

    /// <summary>Seconds we have spent with this class shooting at us.</summary>
    public double SecondsUnderFire;

    /// <summary>Incoming damage per second while engaged — the other half of the TTK race.</summary>
    public float IncomingDps => SecondsUnderFire > 1 ? DamageTaken / (float)SecondsUnderFire : 0f;
}

/// <summary>A shot we fired that is waiting to be resolved into a hit or a miss.</summary>
internal sealed class PendingShot
{
    public ushort AbilityId;
    public uint TargetId;
    public float Distance;
    public float ThrottleFraction;
    public DateTime FiredAt;
    public bool Resolved;
}

/// <summary>Hit rate observed in one distance band.</summary>
public sealed class RangeBucket
{
    public float Low, High;
    public int Shots, Hits;
    public float HitRate => Shots > 0 ? (float)Hits / Shots : 0f;
}

/// <summary>
/// The bot's memory of combat, built from two messages the server was already sending and the
/// bot was already discarding.
///
/// <para><b>Why this exists.</b> Everything a combat decision needs — how long this class takes
/// to kill, how fast it kills us, where our guns actually land — is measurable, and none of it
/// was being measured. The alternative is constants copied from a different server's source,
/// which is a good way to be confidently wrong.</para>
///
/// <para><b>What feeds it.</b> <c>Reply.CombatInfo</c> reports every hit involving us, with the
/// far end's object id, the amount, and whether it crit or killed. <c>Reply.WeaponShot</c> is
/// broadcast for every discharge in the sector, so it also shows fights we are not part of.</para>
///
/// <para><b>Keyed by class, not by contact.</b> Individual NPCs are disposable; their card guid
/// is not. Rolling up by class is what makes the third encounter with a hull informed by the
/// first two.</para>
/// </summary>
public sealed class CombatLog
{
    private readonly object _gate = new();

    private readonly Dictionary<uint, ClassRecord> _byClass = [];

    /// <summary>Shots fired and not yet resolved, oldest first.</summary>
    private readonly List<PendingShot> _pending = [];

    /// <summary>Who each shooter was last seen shooting at, and when it changed. This is how
    /// the NPC re-target cadence gets measured instead of assumed.</summary>
    private readonly Dictionary<uint, (uint Target, DateTime Since)> _lastTargetOf = [];
    private readonly List<double> _retargetIntervals = [];

    /// <summary>When each attacker last hit us, for accumulating time-under-fire.</summary>
    private readonly Dictionary<uint, DateTime> _lastHitFrom = [];

    private readonly RangeBucket[] _buckets;

    private bool _sawAnyDamage;

    /// <summary>
    /// Incoming hits paired with the throttle we were holding, bucketed by tenth of full speed.
    ///
    /// The hypothesis under test is that a target's avoidance scales with its own throttle — so
    /// sitting still is the most hittable a ship ever gets. If that holds here, the low buckets
    /// will show a visibly higher rate of incoming hits than the high ones.
    /// </summary>
    private readonly int[] _incomingByThrottle = new int[11];
    private readonly float[] _incomingDamageByThrottle = new float[11];

    public event Action<string>? Log;

    /// <summary>Resolves a target id to the class it belongs to. Set by the owner.</summary>
    public Func<uint, (uint CardGuid, string Name)>? ClassOf { get; set; }

    /// <summary>How long a shot waits for a damage report before it counts as a miss. Generous:
    /// a projectile has flight time, and calling a hit a miss poisons the curve we are building.</summary>
    public TimeSpan ShotTimeout { get; set; } = TimeSpan.FromSeconds(2.5);

    public long ShotsSeen { get; private set; }
    public long HitsResolved { get; private set; }
    public long MissesResolved { get; private set; }
    public float TotalDealt { get; private set; }
    public float TotalTaken { get; private set; }
    public float TotalRepaired { get; private set; }

    public CombatLog(float maxRange = 4000f, int bucketCount = 8)
    {
        _buckets = new RangeBucket[bucketCount];
        float step = maxRange / bucketCount;
        for (int i = 0; i < bucketCount; i++)
            _buckets[i] = new RangeBucket { Low = i * step, High = (i + 1) * step };
    }

    // ------------------------------------------------------------------ intake

    /// <summary>
    /// Called when the bot pulls a trigger, before the server has said anything.
    ///
    /// The range and throttle at the moment of firing are the whole point: the damage report
    /// comes back with no idea where we were standing, so unless it is recorded here the
    /// hit-rate-versus-range curve cannot be reconstructed afterwards.
    /// </summary>
    public void NoteShotFired(ushort abilityId, uint targetId, float distance, float throttleFraction)
    {
        lock (_gate)
        {
            _pending.Add(new PendingShot
            {
                AbilityId = abilityId,
                TargetId = targetId,
                Distance = distance,
                ThrottleFraction = throttleFraction,
                FiredAt = DateTime.UtcNow,
            });
        }
    }

    /// <summary>Every discharge in the sector — ours, theirs, and other people's.</summary>
    public void OnShot(ShotEvent shot)
    {
        lock (_gate)
        {
            ShotsSeen++;
            if (shot.Target == 0) return;

            if (_lastTargetOf.TryGetValue(shot.Shooter, out var prev))
            {
                if (prev.Target != shot.Target)
                {
                    // A switch. The gap is how long that shooter held its previous target,
                    // which is the observable form of the server's re-target interval.
                    double held = (shot.At - prev.Since).TotalSeconds;
                    if (held is > 0.1 and < 120) _retargetIntervals.Add(held);
                    _lastTargetOf[shot.Shooter] = (shot.Target, shot.At);
                }
            }
            else
            {
                _lastTargetOf[shot.Shooter] = (shot.Target, shot.At);
            }
        }
    }

    /// <summary>One hit or repair involving us.</summary>
    public void OnCombat(CombatEvent ev, float myThrottleFraction)
    {
        if (ev.IsRepair)
        {
            lock (_gate) TotalRepaired += ev.Amount;
            return;
        }

        string? announce = null;

        lock (_gate)
        {
            var (cardGuid, name) = ClassOf?.Invoke(ev.Other) ?? (0u, "");
            var rec = Record(cardGuid, name);

            // Worth one line: it is the proof the whole telemetry path is live, and until it
            // appears there is no way to tell "no fights yet" from "not wired up".
            if (!_sawAnyDamage)
            {
                _sawAnyDamage = true;
                announce = "Combat telemetry is live — the server reports every hit we deal and take.";
            }

            if (ev.FromMe)
            {
                TotalDealt += ev.Amount;
                rec.DamageDealt += ev.Amount;
                rec.HitsDealt++;
                if (ev.Critical) rec.CritsDealt++;
                if (ev.Destroyed)
                {
                    rec.Killed++;
                    if (rec.Killed == 1)
                        announce = $"First kill of {(name.Length > 0 ? name : $"card {cardGuid}")}"
                                 + $" — cost {rec.DamageDealt:F0} damage over {rec.HitsDealt} hit(s).";
                }

                ResolveHit(ev.Other);
            }
            else
            {
                TotalTaken += ev.Amount;
                rec.DamageTaken += ev.Amount;
                rec.HitsTaken++;
                if (ev.Critical) rec.CritsTaken++;
                if (ev.Destroyed) rec.KilledUs++;

                // Time under fire accrues only across hits close enough together to be one
                // engagement; a gap means the fight stopped, not that it lasted all night.
                if (_lastHitFrom.TryGetValue(ev.Other, out var last))
                {
                    double gap = (ev.At - last).TotalSeconds;
                    if (gap is > 0 and < 10) rec.SecondsUnderFire += gap;
                }
                _lastHitFrom[ev.Other] = ev.At;

                int slot = Math.Clamp((int)MathF.Round(myThrottleFraction * 10f), 0, 10);
                _incomingByThrottle[slot]++;
                _incomingDamageByThrottle[slot] += ev.Amount;
            }
        }

        // Outside the lock: a subscriber writing to the UI must not be holding it.
        if (announce is not null) Log?.Invoke(announce);
    }

    /// <summary>
    /// Ages out shots that were never answered and books them as misses.
    ///
    /// A miss produces no message at all — the server's <c>internalProcess</c> simply returns —
    /// so silence is the only evidence there is, and it has to be waited for.
    /// </summary>
    public void Tick()
    {
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                var s = _pending[i];
                if (s.Resolved) { _pending.RemoveAt(i); continue; }
                if (now - s.FiredAt < ShotTimeout) continue;

                var b = Bucket(s.Distance);
                if (b is not null) b.Shots++;
                MissesResolved++;
                _pending.RemoveAt(i);
            }
        }
    }

    /// <summary>Matches a damage report to the oldest unresolved shot at that target.</summary>
    private void ResolveHit(uint targetId)
    {
        for (int i = 0; i < _pending.Count; i++)
        {
            var s = _pending[i];
            if (s.Resolved || s.TargetId != targetId) continue;

            s.Resolved = true;
            var b = Bucket(s.Distance);
            if (b is not null) { b.Shots++; b.Hits++; }
            HitsResolved++;
            return;
        }
    }

    private RangeBucket? Bucket(float distance)
    {
        foreach (var b in _buckets)
            if (distance >= b.Low && distance < b.High) return b;
        return null;
    }

    private ClassRecord Record(uint cardGuid, string name)
    {
        if (!_byClass.TryGetValue(cardGuid, out var rec))
        {
            rec = new ClassRecord { CardGuid = cardGuid, Name = name };
            _byClass[cardGuid] = rec;
        }
        if (rec.Name.Length == 0 && name.Length > 0) rec.Name = name;
        return rec;
    }

    // ------------------------------------------------------------------ readout

    public ClassRecord? ForClass(uint cardGuid)
    {
        lock (_gate) return _byClass.TryGetValue(cardGuid, out var r) ? r : null;
    }

    public IReadOnlyList<ClassRecord> Classes()
    {
        lock (_gate) return _byClass.Values.OrderByDescending(c => c.DamageDealt).ToList();
    }

    /// <summary>Median observed gap between a shooter changing targets, or null with too few
    /// samples to mean anything.</summary>
    public double? MedianRetargetSeconds()
    {
        lock (_gate)
        {
            if (_retargetIntervals.Count < 8) return null;
            var sorted = _retargetIntervals.OrderBy(x => x).ToList();
            return sorted[sorted.Count / 2];
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _pending.Clear();
            _lastTargetOf.Clear();
            _lastHitFrom.Clear();
        }
    }

    /// <summary>
    /// Returns a finished list rather than a lazy sequence, on purpose.
    ///
    /// This used to be an iterator with the whole body inside <c>lock (_gate)</c>. A
    /// <c>yield return</c> inside a lock does not release it between elements — the monitor is
    /// held from the first <c>MoveNext</c> until the enumerator is disposed. So the diagnostics
    /// panel, enumerating this on the UI thread four times a second, held the combat log's lock
    /// across arbitrary caller code, blocking the network thread trying to record a hit. An
    /// enumerator abandoned without disposal would have held it forever.
    /// </summary>
    public IReadOnlyList<string> Describe()
    {
        var lines = new List<string>();
        lock (_gate)
        {
            lines.Add($"combat log     dealt {TotalDealt:F0}, taken {TotalTaken:F0}"
                    + (TotalRepaired > 0 ? $", repaired {TotalRepaired:F0}" : ""));
            lines.Add($"shots resolved {HitsResolved} hit, {MissesResolved} missed"
                    + (HitsResolved + MissesResolved > 0
                        ? $" ({(float)HitsResolved / (HitsResolved + MissesResolved):P0})"
                        : " (nothing fired yet)"));

            var live = _buckets.Where(b => b.Shots > 0).ToList();
            if (live.Count > 0)
            {
                lines.Add("hit rate by range");
                foreach (var b in live)
                    lines.Add($"  {b.Low,5:F0}-{b.High,-5:F0} {b.HitRate,6:P0}  ({b.Hits}/{b.Shots})");
            }

            int incoming = _incomingByThrottle.Sum();
            if (incoming >= 20)
            {
                // Presented raw rather than as a fitted curve: the sample is uncontrolled, and
                // a number that looks derived invites more trust than it has earned.
                lines.Add("incoming hits by our throttle");
                for (int i = 0; i <= 10; i++)
                {
                    if (_incomingByThrottle[i] == 0) continue;
                    lines.Add($"  {i * 10,3}% {_incomingByThrottle[i],4} hit(s), "
                            + $"{_incomingDamageByThrottle[i]:F0} damage");
                }
            }

            // Inlined rather than calling MedianRetargetSeconds(), which takes the same lock —
            // harmless while Monitor is reentrant, but it stops being harmless the moment
            // somebody swaps _gate for a non-reentrant primitive.
            if (_retargetIntervals.Count >= 8)
            {
                var sorted = _retargetIntervals.OrderBy(x => x).ToList();
                lines.Add($"enemy retarget median {sorted[sorted.Count / 2]:F1}s "
                        + $"over {_retargetIntervals.Count} switch(es)");
            }
        }
        return lines;
    }

    /// <summary>One line per class we have actually fought.</summary>
    public IEnumerable<string> DescribeClasses(int limit = 8)
    {
        foreach (var c in Classes().Take(limit))
        {
            string name = c.Name.Length > 0 ? c.Name : $"card {c.CardGuid}";
            yield return $"  {name} — killed {c.Killed}, "
                       + (c.Killed > 0 ? $"{c.DamagePerKill:F0} damage each, " : "")
                       + $"took {c.DamageTaken:F0} from it"
                       + (c.IncomingDps > 0 ? $" ({c.IncomingDps:F0} dps)" : "")
                       + (c.KilledUs > 0 ? $", killed us {c.KilledUs}x" : "");
        }
    }
}
