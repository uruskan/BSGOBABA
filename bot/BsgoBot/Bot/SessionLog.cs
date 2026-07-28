using System.Text.Json;
using System.Text.Json.Serialization;
using BsgoBot.Protocol;
using BsgoBot.World;

namespace BsgoBot.Bot;

/// <summary>
/// One farm run: when it ran, where, in what hull, and what it actually banked. The point of
/// keeping these is comparison — the same rock field flown by two loadouts settles which one
/// mines better, and only a record survives the argument.
/// </summary>
public sealed class FarmSession
{
    public DateTime StartedUtc { get; set; }
    public DateTime? EndedUtc { get; set; }

    /// <summary>When this record was last written to disk. A run that never got an end —
    /// a crash, a kill from task manager — is closed at this stamp on the next load.</summary>
    public DateTime SavedUtc { get; set; }

    public uint SectorId { get; set; }
    public string Ship { get; set; } = "";
    public uint ShipGuid { get; set; }
    public int Deaths { get; set; }

    /// <summary>Units gained per item guid while the run was on — true deltas, not stack totals.</summary>
    public Dictionary<uint, long> Gained { get; set; } = new();

    [JsonIgnore]
    public bool Running => EndedUtc is null;

    /// <summary>Ore only — the three things a rock can hold. Loot and rewards land in
    /// <see cref="Gained"/> too, and belong in earnings, not in a mining comparison.</summary>
    [JsonIgnore]
    public long Mined => Resources.Minable.Sum(m => Gained.GetValueOrDefault((uint)m));

    [JsonIgnore]
    public long TotalGained => Gained.Values.Sum();

    /// <summary>The run's ore priced in cubits (<see cref="Resources.CubitsPerUnit"/>), so a
    /// tylium run and a water run compare on one number instead of three.</summary>
    [JsonIgnore]
    public double CubitValue => Resources.Minable.Sum(
        m => Gained.GetValueOrDefault((uint)m) * Resources.CubitsPerUnit(m));

    public TimeSpan Duration(DateTime nowUtc) => (EndedUtc ?? nowUtc) - StartedUtc;

    public double? OrePerHour(DateTime nowUtc)
    {
        double hours = Duration(nowUtc).TotalHours;
        return hours < 1.0 / 60.0 ? null : Mined / hours;
    }
}

/// <summary>
/// The historic record of farm runs, persisted as JSON beside the config so it survives the
/// process. Begin/End bracket exactly the time the farm loop owned the ship; gains are only
/// attributed while a run is open.
/// </summary>
public sealed class SessionLog
{
    private readonly Lock _gate = new();
    private readonly List<FarmSession> _done = [];
    private FarmSession? _current;
    private string? _path;
    private bool _dirty;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Runs shorter than this with nothing banked are misclicks, not experiments,
    /// and are not worth a row in the history.</summary>
    private const double KeepSeconds = 60;

    public event Action<string>? Log;

    public FarmSession? Current { get { lock (_gate) return _current; } }

    /// <summary>Every finished run plus the live one, newest first.</summary>
    public List<FarmSession> All()
    {
        lock (_gate)
        {
            var list = new List<FarmSession>(_done.Count + 1);
            if (_current is not null) list.Add(_current);
            for (int i = _done.Count - 1; i >= 0; i--) list.Add(_done[i]);
            return list;
        }
    }

    public void Open(string path)
    {
        lock (_gate)
        {
            _path = path;
            _done.Clear();
            try
            {
                if (File.Exists(path))
                {
                    var loaded = JsonSerializer.Deserialize<List<FarmSession>>(File.ReadAllText(path));
                    if (loaded is not null) _done.AddRange(loaded);
                }
            }
            catch (Exception ex)
            {
                Log?.Invoke($"Session history unreadable ({ex.Message}) — starting a fresh one.");
            }

            // A run the process died inside has no end; the last save is the closest honest one.
            foreach (var s in _done)
                if (s.EndedUtc is null)
                {
                    s.EndedUtc = s.SavedUtc > s.StartedUtc ? s.SavedUtc : s.StartedUtc;
                    _dirty = true;
                }
        }
    }

    /// <summary>The owner's lifetime death count when the run began, so the run can record
    /// only the deaths it caused.</summary>
    private int _deathsAtBegin;

    public void Begin(uint sectorId, string ship, uint shipGuid, int deathsNow = 0)
    {
        lock (_gate)
        {
            if (_current is not null) EndLocked();
            _deathsAtBegin = deathsNow;
            _current = new FarmSession
            {
                StartedUtc = DateTime.UtcNow,
                SectorId = sectorId,
                Ship = ship,
                ShipGuid = shipGuid,
            };
            _dirty = true;
        }
        Save();
    }

    public void End()
    {
        lock (_gate) EndLocked();
        Save();
    }

    private void EndLocked()
    {
        if (_current is null) return;
        _current.EndedUtc = DateTime.UtcNow;

        // Keep it only if it says something: a minute of trying, or anything banked at all.
        if (_current.TotalGained > 0
            || _current.Duration(DateTime.UtcNow).TotalSeconds >= KeepSeconds)
            _done.Add(_current);

        _current = null;
        _dirty = true;
    }

    /// <summary>Attribute a hold gain to the live run. Quietly nothing when no run is open —
    /// ore mined by hand between runs is not the bot's to claim.</summary>
    public void OnGained(IReadOnlyList<LootItem> items)
    {
        lock (_gate)
        {
            if (_current is null) return;
            foreach (var it in items)
            {
                if (it.Count == 0) continue;
                _current.Gained[it.CardGuid] =
                    _current.Gained.GetValueOrDefault(it.CardGuid) + it.Count;
            }
            _dirty = true;
        }
    }

    /// <summary>Fills in context that was unknown when the run began — the sector is only ever
    /// named on a scene change, and the ship name can arrive after Start was pressed.</summary>
    public void NoteContext(uint sectorId, string ship, uint shipGuid, int deaths)
    {
        lock (_gate)
        {
            if (_current is null) return;
            if (_current.SectorId == 0 && sectorId != 0) { _current.SectorId = sectorId; _dirty = true; }
            if (_current.Ship.Length == 0 && ship.Length > 0) { _current.Ship = ship; _dirty = true; }
            if (_current.ShipGuid == 0 && shipGuid != 0) { _current.ShipGuid = shipGuid; _dirty = true; }
            int inRun = Math.Max(0, deaths - _deathsAtBegin);
            if (inRun != _current.Deaths) { _current.Deaths = inRun; _dirty = true; }
        }
    }

    /// <summary>Writes only when something changed — safe to call on a slow timer, so a crash
    /// mid-run loses minutes of history rather than the whole run.</summary>
    public void SaveIfDirty()
    {
        bool dirty;
        lock (_gate) dirty = _dirty;
        if (dirty) Save();
    }

    private void Save()
    {
        string? path;
        string json;
        lock (_gate)
        {
            path = _path;
            if (path is null) return;

            var now = DateTime.UtcNow;
            var list = new List<FarmSession>(_done);
            if (_current is not null) { _current.SavedUtc = now; list.Add(_current); }
            foreach (var s in _done) if (s.SavedUtc == DateTime.MinValue) s.SavedUtc = now;

            json = JsonSerializer.Serialize(list, JsonOptions);
            _dirty = false;
        }

        try
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Could not save session history: {ex.Message}");
        }
    }
}
