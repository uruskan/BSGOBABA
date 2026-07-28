using BsgoBot.Cards;
using BsgoBot.Protocol;

namespace BsgoBot.Bot;

public sealed partial class FarmBot
{
    // ---- getting to the target sector -------------------------------------------------
    //
    // A death in a sector with no friendly outpost respawns the ship somewhere else, and an
    // undock starts wherever the hangar was. With BotTuning.TargetSectorId set, neither of
    // those quietly moves the farm: the bot notices the sector mismatch and jumps back, hop
    // by hop, before touching a rock.
    //
    // The route is computed, not configured. The galaxy-map card states every sector's 2D
    // position, the FtlRange stat states how far one jump reaches, and the client's own
    // CanJump test is a plain magnitude comparison between the two — so reachability is a
    // graph, and the route is a shortest-path search over it. A ship with a longer FTL range
    // simply gets a route with fewer legs, with nothing hardcoded per ship.

    /// <summary>The hop last asked for, and when. Zero when no jump is in flight.</summary>
    private uint _hopAsked;
    private DateTime _hopAskedAt = DateTime.MinValue;

    /// <summary>How often the same hop has been asked for without the sector changing. A jump
    /// that will not happen after this many asks is being refused, not delayed.</summary>
    private int _hopAsks;

    /// <summary>Travel stopped deliberately — no route, or the server refused the jump. The bot
    /// farms where it is instead of asking forever; Go farm re-arms it.</summary>
    private bool _travelGaveUp;

    private bool _sectorUnknownSaid;

    /// <summary>Seconds one asked-for hop is given to charge and land before it is re-asked.
    /// The FTL charge itself runs ~10s, and the sector load on top of it is not instant.</summary>
    private const int HopPatienceSeconds = 45;

    private const int MaxHopAsks = 3;

    /// <summary>
    /// The travel step. True means "the ship is between sectors, nothing else should run".
    /// Ordered below the hull and station guards — a jump charge does not make us bulletproof —
    /// and above every farming decision, because rocks in the wrong sector are not the farm.
    /// </summary>
    private async Task<bool> TravelTickAsync(DateTime now)
    {
        uint target = T.TargetSectorId;
        if (target == 0) return false;

        uint here = _world.CurrentSectorId;
        if (here == 0)
        {
            // The sector is only ever named on a scene change, so a bot attached mid-session
            // has not heard one yet. Farming here is the only honest option — guessing a
            // sector to jump out of is worse than staying.
            if (!_sectorUnknownSaid)
            {
                _sectorUnknownSaid = true;
                Log?.Invoke("Target sector is set, but the current sector is unknown — it is "
                          + "only stated on a dock, jump or respawn. Farming here until one happens.");
            }
            return false;
        }
        _sectorUnknownSaid = false;

        if (here == target)
        {
            if (_hopAsked != 0 || _travelGaveUp) Log?.Invoke($"Arrived in target sector {target}.");
            _hopAsked = 0; _hopAsks = 0; _travelGaveUp = false;
            return false;
        }

        // Warned once and stood down; the warning says how to re-arm.
        if (_travelGaveUp) return false;

        var map = Cards.GalaxyMap;
        if (map is null)
        {
            // The card may already be in the per-server cache from the client browsing the map;
            // otherwise it has to be asked for, and the request queue only drains with card
            // fetching on. Waiting for a request that will never be sent is the worst outcome,
            // so that case stands down and says which switch fixes it.
            if (!T.FetchCatalogue)
            {
                _travelGaveUp = true;
                Log?.Invoke("Travel needs the star map card and \"Fetch cards\" is off — "
                          + "turn it on and press Go farm, or clear the target sector.");
                return false;
            }
            Cards.WantGalaxyMap();
            Status = $"In sector {here}, target is {target} — waiting for the star map card";
            return true;
        }

        float range = _world.ShipStat(ObjectStat.FtlRange) ?? 0f;
        if (range <= 0f)
        {
            Status = $"In sector {here}, target is {target} — waiting for the ship's FTL range stat";
            return true;
        }

        var route = PlanRoute(map, here, target, range);
        if (route is null)
        {
            _travelGaveUp = true;
            Log?.Invoke($"No jump route from sector {here} to {target} with an FTL range of "
                      + $"{range:F0} — check the target sector id. Farming here instead; "
                      + "press Go farm to retry.");
            return false;
        }

        uint hop = route[1];
        int legs = route.Count - 1;

        // A hop already asked for gets its charge time before being asked again.
        if (_hopAsked == hop && (now - _hopAskedAt).TotalSeconds < HopPatienceSeconds)
        {
            Status = $"Jumping {here} → {hop}" + (legs > 1 ? $" ({legs} jump(s) to {target})" : "");
            return true;
        }

        if (_hopAsked == hop && _hopAsks >= MaxHopAsks)
        {
            // Three asks, no sector change. Whatever the reason — anchored, out of tylium
            // without the courtesy reply, a server that ignores the id — asking a fourth time
            // will not change it, and NotEnoughTylium has its own, louder handler.
            _travelGaveUp = true;
            Log?.Invoke($"Asked {MaxHopAsks} times to jump from {here} to {hop} and the sector "
                      + "never changed — the server is refusing. Farming here instead; "
                      + "press Go farm to retry.");
            return false;
        }

        if (_hopAsked != hop) { _hopAsked = hop; _hopAsks = 0; }
        _hopAsks++;
        _hopAskedAt = now;

        await _act.FtlJump(hop);
        Log?.Invoke($"FTL jump: sector {here} → {hop}, {legs} jump(s) to reach {target}"
                  + (_hopAsks > 1 ? $" (ask #{_hopAsks})" : "") + ".");
        Status = $"Jumping {here} → {hop}";
        return true;
    }

    /// <summary>
    /// Shortest path over the star map, where two sectors connect when one jump reaches
    /// between them. Total distance is the cost — jumps are priced by distance, so the
    /// cheapest route in tylium and the shortest one are the same route. Returns the full
    /// sector list starting at <paramref name="from"/>, or null when the ids are unknown or
    /// no chain of jumps connects them.
    /// </summary>
    private static List<uint>? PlanRoute(GalaxyMapCardInfo map, uint from, uint to, float range)
    {
        if (map.Star(from) is null || map.Star(to) is null) return null;

        // A few dozen stars — the quadratic scan is simpler than a heap and just as instant.
        var dist = new Dictionary<uint, float> { [from] = 0f };
        var prev = new Dictionary<uint, uint>();
        var done = new HashSet<uint>();

        while (true)
        {
            uint at = 0; float best = float.MaxValue;
            foreach (var (id, d) in dist)
                if (!done.Contains(id) && d < best) { at = id; best = d; }
            if (at == 0) return null;              // frontier empty: unreachable
            if (at == to) break;
            done.Add(at);

            var a = map.Star(at)!;
            foreach (var b in map.Stars)
            {
                if (done.Contains(b.SectorId)) continue;
                float leg = map.Distance(a, b);
                if (leg > range) continue;
                float d = best + leg;
                if (!dist.TryGetValue(b.SectorId, out var known) || d < known)
                {
                    dist[b.SectorId] = d;
                    prev[b.SectorId] = at;
                }
            }
        }

        var route = new List<uint> { to };
        for (uint at = to; at != from; at = prev[at]) route.Add(prev[at]);
        route.Reverse();
        return route;
    }

    /// <summary>The server said the jump costs more tylium than we have. Retrying cannot help
    /// until the hold changes, so travel stands down loudly instead of asking twice more.</summary>
    private void OnNotEnoughTylium()
    {
        if (T.TargetSectorId == 0 || _hopAsked == 0 || _travelGaveUp) return;
        _travelGaveUp = true;
        Log?.Invoke("Not enough tylium for the jump — travel to the target sector stopped. "
                  + "Farming here instead; press Go farm to retry once the hold has refilled.");
    }
}
