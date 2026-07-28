using System.Collections.Concurrent;
using System.Text.Json;
using BsgoBot.Net;

namespace BsgoBot.Cards;

/// <summary>
/// A key into the catalogue. One guid answers to several views, so neither half identifies a
/// card on its own.
/// </summary>
public readonly record struct CardKey(uint Guid, CardView View)
{
    public override string ToString() => $"{Guid}:{(ushort)View}";

    public static bool TryParse(string s, out CardKey key)
    {
        key = default;
        var parts = s.Split(':');
        if (parts.Length != 2) return false;
        if (!uint.TryParse(parts[0], out var guid)) return false;
        if (!ushort.TryParse(parts[1], out var view)) return false;
        key = new CardKey(guid, (CardView)view);
        return true;
    }
}

/// <summary>
/// Builds a local copy of the live server's catalogue.
///
/// The client fetches cards on demand and caches them for the session, so everything it needs
/// passes through the proxy exactly once. That traffic is free to read. What it does *not*
/// cover is anything the client never happens to display — including the ship card of every NPC
/// the bot is about to fight — so the spy also asks for cards itself. An injected request is
/// indistinguishable from the client's own, and the reply is broadcast to the session, meaning
/// the real client simply caches a card it did not ask for.
///
/// <para><b>Raw first, parsed second.</b> Every body is kept verbatim, whether or not a parser
/// exists for that view. A layout we transcribe later can then be applied to cards already on
/// disk instead of needing a fresh capture. This is also what keeps a parser bug from being
/// destructive: the bytes survive it.</para>
///
/// <para><b>Why this replaces guesswork.</b> A ship card states hull, avoidance, armour,
/// accuracy, tier, roles and the type of every slot. The bot currently infers some of that from
/// the live stat stream and asks the user to type in the rest. None of it has to be inferred —
/// the server will simply say, for one request per hull.</para>
/// </summary>
public sealed class CatalogueSpy
{
    /// <summary>Raw bodies, exactly as they arrived.</summary>
    private readonly ConcurrentDictionary<CardKey, byte[]> _raw = new();

    private readonly ConcurrentDictionary<uint, ShipCardInfo> _ships = new();
    private readonly ConcurrentDictionary<uint, WorldCardInfo> _worlds = new();
    private readonly ConcurrentDictionary<uint, ShipAbilityCardInfo> _abilities = new();
    private readonly ConcurrentDictionary<uint, ShipSystemCardInfo> _systems = new();
    private readonly ConcurrentDictionary<uint, ShipListCardInfo> _shipLists = new();
    private readonly ConcurrentDictionary<uint, OwnerCardInfo> _owners = new();

    /// <summary>Cards asked for but not yet answered, with when and how often we have asked.</summary>
    private readonly ConcurrentDictionary<CardKey, Attempt> _pending = new();

    /// <summary>Views whose body we could not decode, so we stop retrying the parse.</summary>
    private readonly ConcurrentDictionary<CardKey, string> _parseFailures = new();

    private readonly object _saveGate = new();
    private string _cachePath = "";
    private bool _dirty;

    public event Action<string>? Log;

    /// <summary>Raised when a card is decoded, so panels can refresh.</summary>
    public event Action<CardKey>? CardLearned;

    public int KnownCards => _raw.Count;
    public int KnownShips => _ships.Count;
    public int KnownWorlds => _worlds.Count;
    public int KnownAbilities => _abilities.Count;
    public int KnownSystems => _systems.Count;
    public int PendingRequests => _pending.Count;
    public long CardsSeenOnWire { get; private set; }
    public long CardsRequested { get; private set; }

    // ------------------------------------------------------------------ lookups

    public ShipCardInfo? Ship(uint guid) => _ships.TryGetValue(guid, out var c) ? c : null;
    public WorldCardInfo? World(uint guid) => _worlds.TryGetValue(guid, out var c) ? c : null;
    public ShipAbilityCardInfo? Ability(uint guid) => _abilities.TryGetValue(guid, out var c) ? c : null;
    public ShipSystemCardInfo? System(uint guid) => _systems.TryGetValue(guid, out var c) ? c : null;
    public ShipListCardInfo? ShipList(uint guid) => _shipLists.TryGetValue(guid, out var c) ? c : null;
    public OwnerCardInfo? Owner(uint guid) => _owners.TryGetValue(guid, out var c) ? c : null;

    /// <summary>The star map, or null until its card has been seen. One per server.</summary>
    public GalaxyMapCardInfo? GalaxyMap { get; private set; }

    /// <summary>Notes that the star map is wanted. Cheap: it is a single cached card.</summary>
    public void WantGalaxyMap() => Want(RootCards.GalaxyMap, CardView.GalaxyMap);

    public bool Has(uint guid, CardView view) => _raw.ContainsKey(new CardKey(guid, view));

    public IReadOnlyCollection<ShipCardInfo> AllShips => _ships.Values.ToList();

    /// <summary>
    /// The ability cards a hull's welded-in weapons grant, resolved through its immutable slots.
    /// Empty until the referenced system and ability cards have arrived, so treat a short list
    /// as "not learned yet" rather than "unarmed".
    /// </summary>
    public IReadOnlyList<ShipAbilityCardInfo> WeaponsOf(uint shipCardGuid)
    {
        var ship = Ship(shipCardGuid);
        if (ship is null) return [];

        var found = new List<ShipAbilityCardInfo>();
        foreach (var slot in ship.ImmutableSlots)
        {
            var sys = System(slot.SystemKey);
            if (sys is null) continue;
            foreach (var abilityGuid in sys.AbilityCardGuids)
                if (Ability(abilityGuid) is { } a) found.Add(a);
        }
        return found;
    }

    // ------------------------------------------------------------------ ingest

    /// <summary>
    /// Feeds one <c>Reply.Card</c>. <paramref name="r"/> must sit at the start of the message
    /// body, i.e. at the card guid.
    /// </summary>
    public void OnCardReply(BgoReader r)
    {
        uint guid = r.ReadGuid();
        var view = (CardView)r.ReadUInt16();
        var body = r.ReadBytes((int)r.Remaining);

        CardsSeenOnWire++;
        Store(new CardKey(guid, view), body, fromWire: true);
    }

    private void Store(CardKey key, byte[] body, bool fromWire)
    {
        _raw[key] = body;
        _pending.TryRemove(key, out _);
        if (fromWire) _dirty = true;

        if (!CardReader.CanParse(key.View)) return;
        if (_parseFailures.ContainsKey(key)) return;

        object? parsed;
        BgoReader r;
        try
        {
            r = new BgoReader(body);
            parsed = key.View switch
            {
                CardView.Ship => CardReader.ReadShip(key.Guid, r),
                CardView.World => CardReader.ReadWorld(key.Guid, r),
                CardView.ShipAbility => CardReader.ReadShipAbility(key.Guid, r),
                CardView.ShipSystem => CardReader.ReadShipSystem(key.Guid, r),
                CardView.ShipList => CardReader.ReadShipList(key.Guid, r),
                CardView.Owner => CardReader.ReadOwner(key.Guid, r),
                CardView.GalaxyMap => CardReader.ReadGalaxyMap(key.Guid, r),
                _ => null,
            };
        }
        catch (Exception ex)
        {
            // Keep the bytes and stop trying. A layout that drifted is worth knowing about, but
            // it must not turn into an exception on every frame.
            _parseFailures[key] = ex.Message;
            Log?.Invoke($"Card {key.Guid} view {key.View} did not parse ({ex.Message}). "
                      + "Bytes kept — re-parsed automatically once the layout is corrected.");
            return;
        }

        if (parsed is null) return;

        // Leftover bytes mean the transcribed layout and the server's no longer agree. Whether
        // the extra field was appended (harmless) or inserted in the middle (everything after it
        // is nonsense) is not something we can tell from here — and a wrong hull figure fed to a
        // combat decision is worse than no figure at all. So the values are dropped and only the
        // bytes are kept, the same rule WhoIsReader applies to half-parsed objects.
        if (r.Remaining != 0)
        {
            _parseFailures[key] = $"{r.Remaining} byte(s) left over";
            Log?.Invoke($"Card {key.Guid} view {key.View} left {r.Remaining} byte(s) unread — "
                      + "layout drift. Values discarded, bytes kept for re-parsing.");
            return;
        }

        switch (parsed)
        {
            case ShipCardInfo card:
                _ships[key.Guid] = card;
                // A hull is only half a description: the world view carries its size and
                // hardpoint geometry, and the systems carry what it shoots with.
                Want(key.Guid, CardView.World);
                foreach (var slot in card.ImmutableSlots)
                    if (slot.SystemKey != 0) Want(slot.SystemKey, CardView.ShipSystem);
                break;

            case WorldCardInfo card:
                _worlds[key.Guid] = card;
                break;

            case OwnerCardInfo card:
                _owners[key.Guid] = card;
                break;

            case ShipAbilityCardInfo card:
                _abilities[key.Guid] = card;
                break;

            case ShipSystemCardInfo card:
                _systems[key.Guid] = card;
                foreach (var abilityGuid in card.AbilityCardGuids)
                    if (abilityGuid != 0) Want(abilityGuid, CardView.ShipAbility);
                break;

            case ShipListCardInfo card:
                _shipLists[key.Guid] = card;
                foreach (var g in card.ShipCardGuids) Want(g, CardView.Ship);
                foreach (var g in card.UpgradeShipCardGuids) Want(g, CardView.Ship);
                break;

            case GalaxyMapCardInfo card:
                GalaxyMap = card;
                break;
        }

        CardLearned?.Invoke(key);
    }

    // ------------------------------------------------------------------ requesting

    /// <summary>
    /// How many times one card is asked for before we accept the answer is "no".
    ///
    /// There must be a limit, because a refusal is indistinguishable from silence. The server
    /// answers an unknown card by logging its own error and sending nothing
    /// (<c>CatalogueProtocol.parseMessage</c>), and it refuses whole categories on purpose —
    /// <c>shipCardFilter</c> drops any ship card with <c>hangarId == -1</c>, which is what every
    /// non-purchasable NPC hull is. Without a cap, each such hull becomes a permanent entry that
    /// re-sends for as long as the session lasts.
    /// </summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>Cards the server has declined often enough that we stop asking.</summary>
    private readonly ConcurrentDictionary<CardKey, byte> _refused = new();

    public int RefusedCards => _refused.Count;

    /// <summary>
    /// Notes that we want a card, if we don't already have it. Does not send anything — the
    /// queue is drained by <see cref="DrainAsync"/> so requests stay rate-limited and off the
    /// traffic-handling path.
    /// </summary>
    public void Want(uint guid, CardView view)
    {
        if (guid == 0) return;
        var key = new CardKey(guid, view);
        if (_raw.ContainsKey(key) || _refused.ContainsKey(key)) return;
        _pending.TryAdd(key, new Attempt(DateTime.MinValue, 0));
    }

    /// <summary>
    /// The two faction rosters, which between them name every player-flyable hull.
    ///
    /// <b>Not called automatically.</b> Each roster cascades into every ship card it names, and
    /// each of those into its world, system and ability cards — thousands of requests, whose
    /// replies are also delivered to the real client, where <c>ShipCard.Read</c> does a
    /// synchronous <c>Resources.Load</c> of the paperdoll layout on Unity's main thread. That is
    /// a lot of unsolicited work to hand a game client that is trying to render a fight.
    ///
    /// Cards for hulls actually encountered arrive one at a time and cost nothing noticeable.
    /// This is the bulk path, and it is opt-in.
    /// </summary>
    public void WantShipRosters()
    {
        Want(RootCards.ColonialShipList, CardView.ShipList);
        Want(RootCards.CylonShipList, CardView.ShipList);
    }

    /// <summary>
    /// Sends up to <paramref name="max"/> outstanding requests as one message, then re-arms
    /// their retry clock and counts the attempt.
    ///
    /// Batched because the client batches: its <c>UpdateMessage</c> packs every card it wants
    /// into a single <c>Request.Card</c>. Matching that shape keeps our traffic looking like
    /// the client's, and costs one frame instead of dozens.
    /// </summary>
    public async Task DrainAsync(Func<IReadOnlyList<CardKey>, Task> send, int max = 8,
                                 TimeSpan? retryAfter = null)
    {
        var wait = retryAfter ?? TimeSpan.FromSeconds(20);
        var now = DateTime.UtcNow;

        var batch = new List<CardKey>(max);
        foreach (var (key, attempt) in _pending)
        {
            if (batch.Count >= max) break;
            if (now - attempt.At <= wait) continue;

            if (attempt.Count >= MaxAttempts)
            {
                // Give up quietly and remember, so a reconnect does not start the cycle again.
                _pending.TryRemove(key, out _);
                _refused[key] = 0;
                continue;
            }

            _pending[key] = new Attempt(now, attempt.Count + 1);
            batch.Add(key);
        }

        if (batch.Count == 0) return;

        await send(batch);
        CardsRequested += batch.Count;
    }

    /// <summary>When a card was last asked for, and how many times.</summary>
    private readonly record struct Attempt(DateTime At, int Count);

    // ------------------------------------------------------------------ persistence

    /// <summary>
    /// Points the cache at a file keyed by server, and loads whatever is already there.
    ///
    /// Per-server on purpose: card guids are only meaningful within the server that issued
    /// them, and mixing two servers' catalogues would produce confidently wrong numbers, which
    /// is worse than having none.
    /// </summary>
    public void OpenCache(string serverKey)
    {
        var safe = string.Concat(serverKey.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
        var dir = Path.Combine(AppContext.BaseDirectory, "cards");
        Directory.CreateDirectory(dir);
        _cachePath = Path.Combine(dir, $"{safe}.json");

        if (!File.Exists(_cachePath))
        {
            Log?.Invoke($"Catalogue cache is new for {serverKey}.");
            return;
        }

        try
        {
            var stored = JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(_cachePath)) ?? [];

            int loaded = 0;
            foreach (var (k, v) in stored)
            {
                if (!CardKey.TryParse(k, out var key)) continue;
                Store(key, Convert.FromBase64String(v), fromWire: false);
                loaded++;
            }
            Log?.Invoke($"Catalogue cache: {loaded} card(s) loaded, "
                      + $"{KnownShips} ship(s), {KnownAbilities} ability(s).");
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Catalogue cache unreadable ({ex.Message}); starting empty.");
        }
    }

    /// <summary>Writes the cache if anything new arrived since the last write.</summary>
    public void SaveCache()
    {
        if (!_dirty || _cachePath.Length == 0) return;

        lock (_saveGate)
        {
            if (!_dirty) return;
            try
            {
                var stored = _raw.ToDictionary(
                    kv => kv.Key.ToString(),
                    kv => Convert.ToBase64String(kv.Value));

                // Write beside the target and move into place, so an interrupted save leaves
                // the previous cache intact rather than a truncated file.
                var tmp = _cachePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(stored));
                File.Move(tmp, _cachePath, overwrite: true);
                _dirty = false;
            }
            catch (Exception ex)
            {
                Log?.Invoke($"Could not save catalogue cache: {ex.Message}");
            }
        }
    }

    // ------------------------------------------------------------------ diagnostics

    public IEnumerable<string> Describe()
    {
        yield return $"cards          {KnownCards} cached, {CardsSeenOnWire} seen on the wire";
        yield return $"ships          {KnownShips} hull(s), {KnownWorlds} model(s)";
        yield return $"systems        {KnownSystems} system(s), {KnownAbilities} ability(s)";
        yield return $"requests       {CardsRequested} sent, {PendingRequests} outstanding"
                   + (RefusedCards > 0 ? $", {RefusedCards} refused by the server" : "");
        if (!_parseFailures.IsEmpty)
            yield return $"unparsed       {_parseFailures.Count} card(s) kept raw";
    }

    /// <summary>One line per known hull, richest fields first. For the diagnostics panel.</summary>
    public IEnumerable<string> DescribeShips(int limit = 40) =>
        _ships.Values
            .OrderBy(s => s.Tier).ThenBy(s => s.CardGuid)
            .Take(limit)
            .Select(s =>
            {
                var world = World(s.CardGuid);
                string name = world?.PrefabName is { Length: > 0 } p ? p : $"#{s.CardGuid}";
                return $"T{s.Tier} {name,-28} hull {s.MaxHull?.ToString("F0") ?? "?",6}"
                     + $"  avoid {s.Avoidance?.ToString("F0") ?? "?",5}"
                     + $"  armor {s.Armor?.ToString("F0") ?? "?",5}"
                     + $"  {s.RoleText}";
            });
}
