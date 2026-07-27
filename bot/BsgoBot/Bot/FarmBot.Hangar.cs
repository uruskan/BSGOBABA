using System.Numerics;
using BsgoBot.Cards;
using BsgoBot.Net;
using BsgoBot.Protocol;
using BsgoBot.Proxy;
using BsgoBot.World;

namespace BsgoBot.Bot;

public sealed partial class FarmBot
{
    // ------------------------------------------------------------------ docking

    /// <summary>
    /// Whether the bot may send a dock request at all.
    ///
    /// On, now that the sequence is right. It was briefly off for good reason: three dock
    /// requests had ever been sent — 02:18:54, 02:34:54 and 13:37:15 on 27 Jul — and the server
    /// hung up 78ms, 364ms and 80ms later, with no case of one working.
    ///
    /// Neither the message nor the range was ever the problem. A real dock captured off the wire
    /// is <c>022D000100004A00000000</c>, which is byte-for-byte what the bot sent, and the same
    /// outpost accepted a manual dock from 791u while the bot was asking from 248u. What was
    /// missing was the LockTarget in front of it — see <see cref="LockBeforeDockAsync"/>.
    ///
    /// Kept as a switch because it is the one action with a proven history of ending sessions.
    /// Turning it off costs nothing but the last step: the retreat still runs to the outpost and
    /// shelters under its guns, which is the part that saves the ship.
    /// </summary>
    public bool AllowDocking { get; set; } = true;

    /// <summary>Until when the server says a docking countdown is running, from Reply.DockingDelay.
    /// The client disables its dock button for exactly this long, so a second request inside the
    /// window is something the real client can never send.</summary>
    private DateTime _dockCountdownUntil = DateTime.MinValue;

    private bool DockCountdownRunning => DateTime.UtcNow < _dockCountdownUntil;

    /// <summary>
    /// Records the docking countdown the server just imposed.
    /// </summary>
    private void NoteDockingDelay(float seconds)
    {
        _dockCountdownUntil = DateTime.UtcNow.AddSeconds(Math.Max(0f, seconds));
        Log?.Invoke($"Server answered with a docking countdown of {seconds:F1}s — "
                  + "holding off any further dock request until it runs out.");
    }

    /// <summary>
    /// Prints one of YOUR dock requests exactly as it left the client.
    ///
    /// Both halves matter. The frame hex settles whether our own dock message is wrong at the
    /// byte level. The message list settles the likelier question: the client only ever docks
    /// <c>GetPlayerTarget()</c>, so a real dock may well be a LockTarget followed by a Dock, and
    /// the bot's retreat clears its target before asking.
    /// </summary>
    private void DumpDockFrame(FrameInfo f)
    {
        Log?.Invoke($"YOUR DOCK — raw frame, {f.Payload.Length}b: {Convert.ToHexString(f.Payload)}");

        var parts = MessageSplitter.Split(f.Payload, fromClient: true);
        Log?.Invoke($"YOUR DOCK — that frame holds {parts.Count} message(s): "
                  + string.Join(" + ", parts.Select(m =>
                        $"{(GameOp.Request)m.MsgType}({m.MsgType}) {m.BodyLength}b")));

        (DateTime At, ushort Type)[] recent;
        lock (_gate) recent = _clientTrail.ToArray();
        if (recent.Length > 0)
        {
            var now = DateTime.UtcNow;
            Log?.Invoke("YOUR DOCK — what the client sent in the seconds before it: "
                      + string.Join(", ", recent.Select(e =>
                            $"-{(now - e.At).TotalSeconds:F1}s {(GameOp.Request)e.Type}")));
        }
    }

    /// <summary>The last few Game requests the real client sent, so a dock can be read in
    /// context rather than in isolation. Bounded and cheap: two fields per entry.</summary>
    private readonly Queue<(DateTime At, ushort Type)> _clientTrail = new();

    private void NoteClientMessage(ushort msgType)
    {
        lock (_gate)
        {
            _clientTrail.Enqueue((DateTime.UtcNow, msgType));
            while (_clientTrail.Count > 16) _clientTrail.Dequeue();
        }
    }

    /// <summary>When we locked the station we are about to dock.</summary>
    private DateTime _dockLockedAt = DateTime.MinValue;

    /// <summary>How long to let a lock settle before docking on the back of it. One tick would
    /// probably do; this is a couple, because the whole point is to stop racing the server.</summary>
    private const double DockLockSettleMs = 600;

    /// <summary>
    /// Selects the station, the way a player does, and says whether the dock must still wait.
    ///
    /// This is the fix for the three sessions that ended within 400ms of a dock request. The
    /// message we sent was byte-for-byte identical to the one the client sends — proven by
    /// dumping a real one: both are <c>022D000100004A00000000</c> — and the range was fine, since
    /// the same outpost accepted a manual dock from 791u while the bot was asking from 248u.
    ///
    /// What differed was everything around it. <c>SpaceLevel.Dock()</c> can only ever dock
    /// <c>GetPlayerTarget()</c>, so a real dock is always a LockTarget followed by a Dock — and
    /// the captured trail shows exactly that, a LockTarget for the outpost twenty seconds ahead of
    /// the dock. The retreat, meanwhile, cleared its target as its first act and asked to dock a
    /// station the server had never been told we had selected. That is a request no client can
    /// produce, and this server answers it by hanging up rather than refusing.
    ///
    /// Returns true while the caller should hold off.
    /// </summary>
    private async Task<bool> LockBeforeDockAsync(SpaceObj station)
    {
        if (_lockedTarget != station.Id)
        {
            await EnsureLocked(station.Id);

            // EnsureLocked declines to lock something the client could not see. Docking on the
            // back of a lock that never went out is the very thing this exists to prevent, so
            // that case waits rather than falling through.
            if (_lockedTarget != station.Id)
            {
                WarnOnce($"cannot lock {station} to dock at it — the lock was refused.");
                return true;
            }

            _dockLockedAt = DateTime.UtcNow;
            lock (_gate) _target = station.Id;
            Log?.Invoke($"Selected {station} — locking before the dock request, which is the "
                      + "order the client itself uses.");
            return true;
        }

        return (DateTime.UtcNow - _dockLockedAt).TotalMilliseconds < DockLockSettleMs;
    }

    /// <summary>How close to get before asking to dock, when nothing has been learned yet.</summary>
    public float DockApproach { get; set; } = 250f;

    /// <summary>Give up on a dock run after this long.</summary>
    public int DockTimeoutSeconds { get; set; } = 90;

    /// <summary>
    /// The shortest time we will spend at a refuge before its hull trend is allowed to send us
    /// away. Hysteresis, not a deadline: one burst of damage on arrival must not abandon an
    /// outpost that is about to let us in.
    /// </summary>
    public float DockGiveUpSeconds { get; set; } = 10f;

    /// <summary>
    /// How much hull we will lose at a refuge before deciding it is not sheltering us.
    ///
    /// This replaced a flat 10s timeout, which was wrong in exactly the case it was written for:
    /// a dock cooldown after combat can run to tens of seconds, and a short timer abandons a good
    /// outpost while its countdown is still ticking. Whether the shelter is working is a thing we
    /// can measure — 0.10 is ten points of hull lost since the best reading since arriving.
    /// </summary>
    public float RefugeBleedFraction { get; set; } = 0.10f;

    public bool IsDocking => _docking;

    /// <summary>
    /// Fly to the nearest station and dock. Stops farming first — you don't want the bot
    /// opening fire on the way in.
    /// </summary>
    public void BeginDock()
    {
        if (_following) StopFollowing();
        if (Enabled) Stop();

        var station = _world.Nearest(o => EntityTypes.IsDockable(o.Id)
                                       && _world.RelationTo(o.Id) is Relation.Friend or Relation.Neutral or Relation.Self);
        if (station is null)
        {
            Status = "Nothing dockable in range — no outpost or capital ship located";
            Log?.Invoke("Dock: no dockable object located in this sector.");
            return;
        }

        _dockTarget = station.Id;
        _docking = true;
        _dockAsked = DateTime.MinValue;
        _dockStarted = DateTime.UtcNow;
        ForgetThrottle();

        Log?.Invoke($"Docking at {station} ({_world.DistanceToMe(station):F0}u away).");
        _timer.Change(0, 250);              // the dock run needs the loop even with farming off
    }

    public void CancelDock()
    {
        if (!_docking) return;
        _docking = false;
        _dockTarget = 0;
        _ = _act.CancelDocking();
        _ = StopThrottleIfMoving();
        Status = "Docking cancelled";
        Log?.Invoke("Docking cancelled.");
        if (!Enabled) _timer.Change(Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>
    /// Get out — of a hangar OR of a carrier, which are different messages.
    ///
    /// The client's UNDOCK button tests anchoring FIRST and only reaches <c>Room.Quit</c> if it
    /// is not set (<c>UndockButton.Undock</c>). Sending the hangar message while riding a carrier
    /// is the wrong message for the state, which is where this started.
    /// </summary>
    public void Undock()
    {
        _docking = false;
        _dockTarget = 0;
        _lastLaunchAsk = DateTime.UtcNow;

        if (_world.Anchored)
        {
            _ = _act.RequestUnanchor();
            Status = "Launching from the carrier";
            Log?.Invoke($"Unanchor requested — riding #{_world.AnchoredTo:X8}.");
            return;
        }

        _ = _act.LeaveRoom();
        Status = "Undocking";
        Log?.Invoke("Undock requested (Room.Quit).");
    }

    // ------------------------------------------------------------------ death & relaunch

    /// <summary>
    /// Get the ship back out of the hangar by itself: answer the death screen, buy the hull
    /// condition back, launch, and carry on farming.
    ///
    /// Off means a death ends the session in every practical sense — the farm loop keeps ticking
    /// against a ship that is not in the sector and does nothing at all until someone presses
    /// Undock.
    /// </summary>
    public bool AutoUndock { get; set; } = true;

    /// <summary>Buy the ship's condition back before launching, with titanium. Dying always costs
    /// condition, and a wrecked hull launches with a fraction of its stats.</summary>
    public bool AutoRepair { get; set; } = true;

    /// <summary>
    /// How long to sit in the hangar before launching.
    ///
    /// Not politeness: the client has its own death sequence to play out, the repair has to be
    /// asked for and answered, and the server will not launch a ship it still thinks is dead. Six
    /// seconds is enough for all three without making a death cost a minute of farming.
    /// </summary>
    public int UndockDelaySeconds { get; set; } = 6;

    /// <summary>How long to wait before asking to launch again when the first ask changed nothing.</summary>
    public int RelaunchIntervalSeconds { get; set; } = 15;

    /// <summary>Times we've been destroyed this session.</summary>
    public int Deaths { get; private set; }

    /// <summary>Times the bot has bought the hull back this session.</summary>
    public int RepairsBought { get; private set; }

    /// <summary>True while the ship is out of the sector — dead, docked, or jumping.</summary>
    public bool InHangar => _hangarSince is not null;

    /// <summary>Condition of the ship we're flying against what its card says it should be, or
    /// null while either half is unknown.</summary>
    public (float Now, float Max)? Condition
    {
        get
        {
            if (_world.MyCondition is not { } now) return null;
            uint guid = _world.MyShipGuid;
            if (guid == 0 || Cards.Ship(guid)?.Durability is not { } max || max <= 0) return null;
            return (now, max);
        }
    }

    /// <summary>Whether the hull is worth paying to patch. Null when we cannot tell — no card, or
    /// no ShipInfo — which is a different answer from "no".</summary>
    private bool? ConditionShort() =>
        Condition is { } c ? c.Now < c.Max * 0.999f : null;

    private void OnRespawnOffered(IReadOnlyList<(uint SectorId, uint CarrierPlayerId)> options)
    {
        _respawnOffer = options;
        _lastRespawnOffer = options;
        _diedHere = true;
        Log?.Invoke($"Death screen: {options.Count} respawn location(s) offered"
                  + (AutoUndock ? "." : " — auto undock is off, so pick one in the client."));
    }

    private void OnShipCondition(ushort shipId, float durability)
    {
        if (shipId != _world.MyShipId || !_repairAsked) return;

        // The server answering a repair is the only proof it took it. Comparing against what we
        // saw before we asked keeps a routine ShipInfo from being read as a successful repair.
        if (_conditionBeforeRepair is { } was && durability > was + 0.01f)
        {
            RepairsBought++;
            _repairWarned = true;                 // it worked; nothing to warn about
            string of = Condition is { } c ? $" of {c.Max:F0}" : "";
            Log?.Invoke($"Repaired: condition {was:F0} → {durability:F0}{of}.");
        }
    }

    /// <summary>Forget the whole hangar sequence. Called once the ship is flying again.</summary>
    private void ClearHangarState()
    {
        _hangarSince = null;
        _respawnOffer = null;
        // Dropped with the rest: a death screen from an earlier death must never be answered
        // during an ordinary dock later on.
        _lastRespawnOffer = null;
        _respawnAnswered = DateTime.MinValue;
        _launchAsks = 0;
        _lastLaunchAsk = DateTime.MinValue;
        _repairAsked = false;
        _repairWarned = false;
        _conditionBeforeRepair = null;
        _diedHere = false;
    }

    /// <summary>True while we are riding a carrier rather than flying our own ship.</summary>
    public bool IsAnchored => _world.Anchored;

    private DateTime? _anchoredSince;
    private DateTime _lastUnanchorAsk = DateTime.MinValue;
    private int _unanchorAsks;

    /// <summary>Wait this long after anchoring before asking to launch, so a carrier you boarded
    /// on purpose is not immediately thrown off it.</summary>
    public int UnanchorDelaySeconds { get; set; } = 4;

    private void OnAnchorChanged(uint carrier)
    {
        if (carrier != 0)
        {
            _anchoredSince = DateTime.UtcNow;
            _lastUnanchorAsk = DateTime.MinValue;
            _unanchorAsks = 0;

            // Everything we might have had running belongs to a ship we are no longer flying.
            Weapons.ResetToggles();
            ForgetThrottle();
            if (_docking) { _docking = false; _dockTarget = 0; }
            lock (_gate) { _target = 0; _lockedTarget = 0; }

            var owner = _world.Get(carrier);
            Log?.Invoke($"Anchored to {(owner is not null ? owner.ToString() : $"#{carrier:X8}")}"
                      + " — riding, not flying. No steering, no firing, no docking until we launch.");
            return;
        }

        _anchoredSince = null;
        _unanchorAsks = 0;
        Log?.Invoke("Off the carrier — flying our own ship again.");
    }

    /// <summary>
    /// The carrier state. Blocks the farm loop outright and, when farming, asks to launch.
    ///
    /// Blocking is the important half. While anchored the client disables its whole ability bar
    /// and every flight control, because the ship is a passenger — so throttle, heading, casts and
    /// dock requests are all traffic no real client can produce in that state. The bot sent them
    /// anyway, for six seconds, ending in a Dock request from inside somebody's Brimir; the server
    /// closed the connection on the spot.
    ///
    /// Returns true whenever we are anchored, so nothing downstream gets the tick.
    /// </summary>
    private async Task<bool> AnchorTickAsync()
    {
        if (!_world.Anchored) return false;

        var now = DateTime.UtcNow;
        _anchoredSince ??= now;

        var carrier = _world.Get(_world.AnchoredTo);
        string riding = carrier is not null ? carrier.ToString() : $"#{_world.AnchoredTo:X8}";

        if (!AutoUndock || !Enabled)
        {
            Status = $"Anchored to {riding} — riding along";
            return true;
        }

        double waited = (now - _anchoredSince.Value).TotalSeconds;
        if (waited < UnanchorDelaySeconds)
        {
            Status = $"Anchored to {riding} — launching in {UnanchorDelaySeconds - waited:F0}s";
            return true;
        }

        if ((now - _lastUnanchorAsk).TotalSeconds >= RelaunchIntervalSeconds)
        {
            _lastUnanchorAsk = now;
            _unanchorAsks++;
            await _act.RequestUnanchor();
            Log?.Invoke(_unanchorAsks == 1
                ? $"Launching from {riding} (RequestUnanchor) to carry on farming."
                : $"Still aboard {riding} — asking to launch again (attempt {_unanchorAsks}).");
        }

        Status = $"Launching from {riding}"
               + (_unanchorAsks > 1 ? $" — asked {_unanchorAsks}x" : "");
        return true;
    }

    /// <summary>
    /// The whole out-of-sector state machine, in the order the server needs it: answer the death
    /// screen, repair, then launch — and keep asking to launch, because the first ask can land
    /// while the server still has us dead.
    ///
    /// Returns true when it has taken the tick, i.e. the ship is not in the sector and the bot is
    /// doing something about it.
    /// </summary>
    private async Task<bool> HangarTickAsync()
    {
        bool inSector = _world.MyObjectId != 0 && _world.MyPositionKnown;
        if (inSector)
        {
            if (_hangarSince is { } since)
            {
                Log?.Invoke($"Flying again after {(DateTime.UtcNow - since).TotalSeconds:F0}s out of the sector.");

                // A death does not always arrive as RemoveMe — the ship can leave as a plain
                // ObjectLeft, in which case OnSectorLeft never ran and every piece of in-flight
                // bookkeeping survived the grave. The approach watchdog proved it: a
                // best-distance from the previous life plus a respawn somewhere else read as
                // "no progress for 38s", and a perfectly good rock was skipped the same second
                // the new ship entered space. This is the new ship's first tick: whatever the
                // old one was doing, it is not doing it any more. World knowledge — rocks,
                // scans, skips — is still true and stays.
                Weapons.ResetToggles();
                ForgetThrottle();
                _mineWatchId = 0;
                _holdId = 0;
                _fixWaitSince = DateTime.MinValue;
                _fixWaitGaveUp = false;
                _movedAt = DateTime.MinValue;
                // Locks and subscriptions belong to the dead ship; force both to be re-sent.
                lock (_gate) { _lockedTarget = 0; _subscribedTarget = 0; }

                ClearHangarState();
            }
            return false;
        }

        // Before the login handshake there is no ship to launch and no hangar to launch it from —
        // that is the "waiting for the handshake" case, not a docked one.
        if (_world.MyPlayerId == 0) return false;

        var now = DateTime.UtcNow;
        _hangarSince ??= now;

        if (!AutoUndock || !Enabled) return false;

        // A death screen blocks everything else: the server will not launch a dead ship, and
        // nothing else answers this message once the bot is flying.
        if (_respawnOffer is { Count: > 0 } offer)
        {
            // A station, not a stranger's carrier. A carrier id of 0 means the option is a place
            // of our own; anything else lands us anchored inside another player's ship, which is
            // a state the bot cannot farm from, cannot leave without their say-so, and used to
            // fly around inside. Taking offer[0] blindly is how we ended up in a Brimir.
            var pick = offer.FirstOrDefault(o => o.CarrierPlayerId == 0, offer[0]);

            _respawnOffer = null;
            _respawnAnswered = now;
            await _act.SelectRespawnLocation(pick.SectorId, pick.CarrierPlayerId);
            Log?.Invoke($"Respawning at sector {pick.SectorId}"
                      + (pick.CarrierPlayerId != 0
                            ? $" (carrier of player {pick.CarrierPlayerId} — no station was offered)"
                            : "")
                      + $" — {offer.Count} location(s) were offered.");
            Status = "Respawning";
            return true;
        }

        // Give the respawn a moment to land before asking the hangar for anything.
        if (_respawnAnswered != DateTime.MinValue && (now - _respawnAnswered).TotalSeconds < 2)
        {
            Status = "Respawning";
            return true;
        }

        if (await RepairInHangarAsync(now)) return true;

        double waited = (now - _hangarSince.Value).TotalSeconds;
        if (waited < UndockDelaySeconds)
        {
            Status = $"In the hangar — launching in {UndockDelaySeconds - waited:F0}s";
            return true;
        }

        // Three launches ignored means the server is not refusing to undock us — it still has us
        // dead, and JumpIn is simply the wrong message. Answer the death screen again.
        if (_launchAsks >= 3 && _lastRespawnOffer is { Count: > 0 }
            && (now - _respawnAnswered).TotalSeconds > 45)
        {
            _respawnOffer = _lastRespawnOffer;
            _launchAsks = 0;
            Log?.Invoke("Three launches changed nothing — answering the death screen again.");
            return true;
        }

        if ((now - _lastLaunchAsk).TotalSeconds >= RelaunchIntervalSeconds)
        {
            _lastLaunchAsk = now;
            _launchAsks++;
            await _act.LeaveRoom();

            // The client sends its own JumpIn once the space level has loaded, so ours is only
            // worth trying after the room has plainly already been left and we are stuck at the
            // last step instead of the first.
            if (_launchAsks >= 2) await _act.JumpIn();

            Log?.Invoke(_launchAsks == 1
                ? "Undocking to carry on farming (Room.Quit)."
                : $"Still in the hangar — Room.Quit and JumpIn again (attempt {_launchAsks}).");
        }

        Status = _launchAsks > 1
            ? $"Undocking — asked {_launchAsks}x, {(now - _hangarSince.Value).TotalSeconds:F0}s in the hangar"
            : "Undocking";
        return true;
    }

    /// <summary>
    /// Buys the hull back before launching. One RepairAll covers the hull and every fitted system,
    /// which is what a death damages — repairing the hull alone launches a ship with dead slots.
    ///
    /// Returns true while it wants the tick to itself, i.e. it just asked and is waiting.
    /// </summary>
    private async Task<bool> RepairInHangarAsync(DateTime now)
    {
        if (!AutoRepair || _world.MyShipId == 0) return false;

        if (!_repairAsked)
        {
            // Repair when we know it is short, or when we died — dying always costs condition,
            // and on a server that sends no ShipInfo that is the only signal there is.
            bool? shortOf = ConditionShort();
            if (shortOf == false || (shortOf is null && !_diedHere)) return false;

            _repairAsked = true;
            _repairAskedAt = now;
            _conditionBeforeRepair = _world.MyCondition;

            // Titanium, never cubits. Cubits are bought with money, and nothing the bot does by
            // itself should be able to spend them.
            await _act.RepairAll(_world.MyShipId, useCubits: false);
            Log?.Invoke(Condition is { } c
                ? $"Repairing ship {_world.MyShipId} — condition {c.Now:F0}/{c.Max:F0}, paying titanium."
                : $"Repairing ship {_world.MyShipId} with titanium (condition unknown).");
            Status = "Repairing";
            return true;
        }

        // Asked, and the server never moved the number. Say so once: the likely causes are no
        // titanium, a hull the server only repairs for cubits, or a server that ignores RepairAll.
        if (!_repairWarned && (now - _repairAskedAt).TotalSeconds > 8)
        {
            _repairWarned = true;
            if (ConditionShort() != false)
                Log?.Invoke("Repair didn't take — the server left the condition where it was. "
                          + "Check titanium, or repair by hand in the damage window.");
        }

        return false;
    }

    private async Task DockTick()
    {
        var station = _world.Get(_dockTarget);
        if (station is null || !station.HasPosition)
        {
            Status = "Lost the station — it left the sector or was never located";
            _docking = false;
            return;
        }

        // A countdown the server itself imposed is not the run failing, it is the run working —
        // so the timeout stands down while one is ticking. A dock delay after combat can be tens
        // of seconds, which would otherwise abandon a dock that was about to complete.
        if (!DockCountdownRunning && (DateTime.UtcNow - _dockStarted).TotalSeconds > DockTimeoutSeconds)
        {
            Status = "Gave up docking — took too long";
            Log?.Invoke("Dock run timed out.");
            _docking = false;
            await StopThrottleIfMoving();
            return;
        }

        float dist = _world.DistanceToMe(station) ?? float.MaxValue;

        float ask = DockRange(station);

        if (dist > ask)
        {
            await SteerToward(station, ask);
            Status = $"Docking — {dist:F0}u to {station}, closing to {ask:F0}u, {SpeedInGear(_gear):F0}u/s {_gear}";
            return;
        }

        await StopThrottleIfMoving();

        // Arrived, but the request itself is the dangerous part — see AllowDocking. The run ends
        // here rather than pretending to continue, because the ship is where it was asked to be.
        if (!AllowDocking)
        {
            _docking = false;
            Status = $"At {station} ({dist:F0}u) — not docking, it drops the session";
            Log?.Invoke($"Arrived at #{station.Id:X8} ({dist:F0}u) but did not send a dock "
                      + "request: every one the bot has sent ended the session within 400ms. "
                      + "Dock by hand — the bot reads your request and learns from it — or set "
                      + "AllowDocking in bot.json.");
            return;
        }

        // Selected first, exactly as the client does — see LockBeforeDockAsync.
        if (await LockBeforeDockAsync(station))
        {
            Status = $"At {station} ({dist:F0}u) — selecting it to dock";
            return;
        }

        // Once per few seconds, not per tick: every rejected attempt writes a line in the
        // server log with your player id. And never inside the server's own countdown, which
        // disables the real client's dock button for its duration.
        if (DockCountdownRunning) return;
        if ((DateTime.UtcNow - _dockAsked).TotalSeconds < 4) return;
        _dockAsked = DateTime.UtcNow;

        await _act.Dock(station.Id);
        Status = $"Dock requested at {station} from {dist:F0}u";
        Log?.Invoke($"Dock requested at #{station.Id:X8} from {dist:F0}u.");
    }

}
