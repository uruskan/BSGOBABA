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

    /// <summary>Launch back into the sector. This is what "undock" is on the wire — the server's
    /// JumpIn handler puts the ship back into the sector it is registered to.</summary>
    public Task JumpIn() => proxy.InjectAsync(Msg(GameOp.Request.JumpIn));

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
}
