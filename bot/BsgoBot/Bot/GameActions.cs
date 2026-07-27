using System.Numerics;
using BsgoBot.Net;
using BsgoBot.Protocol;
using BsgoBot.Proxy;

namespace BsgoBot.Bot;

/// <summary>
/// Outgoing game commands. Every payload below is transcribed field-for-field from the
/// client's GameProtocol, so the server cannot tell these from real client input.
/// </summary>
public sealed class GameActions(GameProxy proxy)
{
    private BgoWriter Msg(GameOp.Request op) => new((byte)ProtocolId.Game, (ushort)op);

    public Task LockTarget(uint objectId)
    {
        var w = Msg(GameOp.Request.LockTarget);
        w.Write(objectId);
        return proxy.InjectAsync(w);
    }

    public Task CastSlotAbility(ushort abilityId, params uint[] targets)
    {
        var w = Msg(GameOp.Request.CastSlotAbility);
        w.Write(abilityId);
        w.WriteIdList(targets);
        return proxy.InjectAsync(w);
    }

    public Task CastImmutableSlotAbility(ushort abilityId, params uint[] targets)
    {
        var w = Msg(GameOp.Request.CastImmutableSlotAbility);
        w.Write(abilityId);
        w.WriteIdList(targets);
        return proxy.InjectAsync(w);
    }

    public Task ToggleAbilityOn(ushort abilityId, params uint[] targets)
    {
        var w = Msg(GameOp.Request.ToggleAbilityOn);
        w.Write(abilityId);
        w.WriteIdList(targets);
        return proxy.InjectAsync(w);
    }

    public Task ToggleAbilityOff(ushort abilityId)
    {
        var w = Msg(GameOp.Request.ToggleAbilityOff);
        w.Write(abilityId);
        return proxy.InjectAsync(w);
    }

    /// <summary>Retargets an already-toggled ability without turning it off and on again —
    /// exactly what the client does when you switch targets with a beam weapon running.</summary>
    public Task UpdateAbilityTargets(ushort abilityId, params uint[] targets)
    {
        var w = Msg(GameOp.Request.UpdateAbilityTargets);
        w.Write(abilityId);
        w.WriteIdList(targets);
        return proxy.InjectAsync(w);
    }

    public Task Mine(uint asteroidId)
    {
        var w = Msg(GameOp.Request.Mining);
        w.Write(asteroidId);
        return proxy.InjectAsync(w);
    }

    public Task CancelMining() => proxy.InjectAsync(Msg(GameOp.Request.CancelMiningRequest));

    public Task RequestLoot(uint objectId)
    {
        var w = Msg(GameOp.Request.Loot);
        w.Write(objectId);
        return proxy.InjectAsync(w);
    }

    /// <summary>Note the client writes a byte-count (items*4), not an item count.</summary>
    public Task TakeLootItems(ushort lootId, IReadOnlyCollection<ushort> itemIds)
    {
        var w = Msg(GameOp.Request.TakeLootItems);
        w.Write(lootId);
        w.Write((ushort)(itemIds.Count * 4));
        foreach (var id in itemIds) w.Write((uint)id);
        return proxy.InjectAsync(w);
    }

    /// <summary>Asks the server to stream this object's stats (hull, power, target).</summary>
    public Task SubscribeInfo(uint objectId)
    {
        var w = Msg(GameOp.Request.SubscribeInfo);
        w.Write(objectId);
        return proxy.InjectAsync(w);
    }

    public Task UnSubscribeInfo(uint objectId)
    {
        var w = Msg(GameOp.Request.UnSubscribeInfo);
        w.Write(objectId);
        return proxy.InjectAsync(w);
    }

    /// <summary>Asks for movement updates on objects we know of but have no position for.</summary>
    public Task MoveInfo(IReadOnlyCollection<uint> objectIds)
    {
        var w = Msg(GameOp.Request.MoveInfo);
        w.WriteIdList(objectIds);
        return proxy.InjectAsync(w);
    }

    public Task SetSpeed(SpeedMode mode, float speed)
    {
        var w = Msg(GameOp.Request.SetSpeed);
        w.Write((byte)mode);
        w.Write(speed);
        return proxy.InjectAsync(w);
    }

    public Task SetGear(Gear gear)
    {
        var w = Msg(GameOp.Request.SetGear);
        w.Write((byte)gear);
        return proxy.InjectAsync(w);
    }

    /// <summary>Euler3 heading: pitch, yaw, roll — the client sends Euler3.Direction(target - me).</summary>
    public Task MoveToDirection(Vector3 euler)
    {
        var w = Msg(GameOp.Request.MoveToDirection);
        w.WriteEuler(euler.X, euler.Y, euler.Z);
        return proxy.InjectAsync(w);
    }

    public Task MoveToDirectionWithoutRoll(Vector3 euler)
    {
        var w = Msg(GameOp.Request.MoveToDirectionWithoutRoll);
        w.WriteEuler(euler.X, euler.Y, euler.Z);
        return proxy.InjectAsync(w);
    }

    public Task Dock(uint objectId, float delay = 0f)
    {
        var w = Msg(GameOp.Request.Dock);
        w.Write(objectId);
        w.Write(delay);
        return proxy.InjectAsync(w);
    }

    public Task CancelDocking() => proxy.InjectAsync(Msg(GameOp.Request.CancelDocking));

    /// <summary>
    /// "I have loaded the sector, put my ship in it." The client sends this from
    /// <c>SpaceLevel.Preload</c>, once every card of every object already in the sector has
    /// arrived — never from a hangar. See <see cref="LeaveRoom"/> for undocking.
    /// </summary>
    public Task JumpIn() => proxy.InjectAsync(Msg(GameOp.Request.JumpIn));

    /// <summary>
    /// Undock. This is the UNDOCK button, field for field: <c>RoomProtocol.Quit</c>, no payload.
    ///
    /// The client's own button does exactly this (<c>UndockButton.Undock</c>) for anyone who is
    /// neither anchored nor sitting in a carrier. Everything after it is the server's move: it
    /// takes us out of the room, the client loads the space level, and the client sends its own
    /// JumpIn when it is ready. So the bot injects one message and then gets out of the way.
    /// </summary>
    public Task LeaveRoom() =>
        proxy.InjectAsync(new BgoWriter((byte)ProtocolId.Room, (ushort)RoomOp.Request.Quit));

    /// <summary>
    /// Get off a carrier. The UNDOCK button sends this — not <see cref="LeaveRoom"/> — whenever
    /// <c>Game.Me.Anchored</c> is set, and it is the first branch it tests. No payload.
    /// </summary>
    public Task RequestUnanchor() => proxy.InjectAsync(Msg(GameOp.Request.RequestUnanchor));

    /// <summary>
    /// Answer the death screen. The pair comes straight out of Reply.RespawnOptions, which sends
    /// two equal-length id lists — sectors and the carrier player each one belongs to (0 for a
    /// station). Client: GameProtocol.SelectRespawnLocation(RespawnLocationInfo).
    /// </summary>
    public Task SelectRespawnLocation(uint sectorId, uint carrierPlayerId)
    {
        var w = Msg(GameOp.Request.SelectRespawnLocation);
        w.Write(sectorId);
        w.Write(carrierPlayerId);
        return proxy.InjectAsync(w);
    }

    private static BgoWriter PlayerMsg(PlayerOp.Request op) => new((byte)ProtocolId.Player, (ushort)op);

    /// <summary>
    /// The damage window's "repair all", for one hangar ship: hull condition and every fitted
    /// system, in a single message. Titanium unless <paramref name="useCubits"/> — and cubits are
    /// real money, so nothing in the bot passes true unless you ask for it.
    /// </summary>
    public Task RepairAll(ushort shipId, bool useCubits = false)
    {
        var w = PlayerMsg(PlayerOp.Request.RepairAll);
        w.Write(shipId);
        w.Write(useCubits);
        return proxy.InjectAsync(w);
    }

    /// <summary>Buy back <paramref name="points"/> of hull condition only — no systems.
    /// Kept because a server that ignores RepairAll may still answer this.</summary>
    public Task RepairShip(ushort shipId, float points, bool useCubits = false)
    {
        var w = PlayerMsg(PlayerOp.Request.RepairShip);
        w.Write(shipId);
        w.Write(points);
        w.Write(useCubits);
        return proxy.InjectAsync(w);
    }

    public Task SendCargoInteraction(uint cargoId, CargoInteraction action)
    {
        var w = Msg(GameOp.Request.CargoInteraction);
        w.Write(cargoId);
        w.Write((byte)action);
        return proxy.InjectAsync(w);
    }

    public Task WhoIs(uint objectId)
    {
        var w = Msg(GameOp.Request.WhoIs);
        w.Write(objectId);
        return proxy.InjectAsync(w);
    }

    /// <summary>
    /// Asks the server for catalogue cards.
    ///
    /// Shaped exactly like the client's <c>CatalogueProtocol.UpdateMessage</c>: a uint16 total
    /// count followed by that many <c>guid, view</c> pairs — note the count is of pairs, and the
    /// client flattens its guid-to-views map into the same flat list.
    ///
    /// The reply is delivered to the session, so the real client receives cards it never asked
    /// for. That is harmless — its <c>ParseMessage</c> loads any incoming card straight into its
    /// own cache — but it is the reason to keep batches modest rather than requesting the whole
    /// catalogue in one burst.
    /// </summary>
    public Task RequestCards(IReadOnlyList<Cards.CardKey> cards)
    {
        var w = new BgoWriter((byte)ProtocolId.Catalogue, (ushort)Cards.CatalogueOp.Request.Card);
        w.Write((ushort)cards.Count);
        foreach (var c in cards)
        {
            w.Write(c.Guid);
            w.Write((ushort)c.View);
        }
        return proxy.InjectAsync(w);
    }
}
