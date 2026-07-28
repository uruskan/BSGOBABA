using System.Numerics;
using BsgoBot.Cards;
using BsgoBot.Net;
using BsgoBot.Protocol;
using BsgoBot.Proxy;
using BsgoBot.World;

namespace BsgoBot.Bot;

public sealed partial class FarmBot
{
    // ------------------------------------------------------------------ traffic

    private void OnSessionStarted()
    {
        _world.Clear();
        Weapons.ResetToggles();
        ForgetThrottle();
        _following = false;
        _followTarget = 0;
        lock (_gate)
        {
            _target = 0; _lockedTarget = 0; _subscribedTarget = 0; _pinned = 0;
            _lootAsked.Clear(); _facilityOrdered.Clear(); _skip.Clear(); _hardSkip.Clear();
            _scanAsked.Clear(); _scanProbe.Clear(); _probed.Clear();
            _scanStrikes.Clear(); _unansweredRocks.Clear(); _dockRefused.Clear();
        }
        _roamTarget = 0;
        _scansWithoutReply = 0;
        _ammoWarned = false;
        _filterAbandoned = false;     // fresh session, so nothing has failed yet — no message

        // The catalogue survives a session — cards are per-server, not per-login — so it is
        // opened rather than cleared.
        //
        // The ship rosters are deliberately NOT requested here. They looked like the cheap way
        // to fill the table, being two guids, but each one names every hull in the game and each
        // hull cascades into its world, system and ability cards. That is thousands of replies,
        // all of which the real client also receives and parses. Cards for hulls we actually
        // meet arrive a couple at a time and cost nothing. Bulk is opt-in, via PrefetchRosters.
        Cards.OpenCache(_proxy.UpstreamKey);
    }

    /// <summary>
    /// Pull the entire ship catalogue in one go.
    ///
    /// Thousands of requests and replies, shared with the real client. Worth doing once on a
    /// quiet dock, not on every login, and not while flying.
    /// </summary>
    public void PrefetchRosters()
    {
        if (!T.FetchCatalogue)
        {
            Log?.Invoke("Card fetching is off — turn on \"Fetch cards\" first.");
            return;
        }
        Cards.WantShipRosters();
        Log?.Invoke("Requested both ship rosters. This will pull a large number of cards; "
                  + "expect the client to be busy for a while.");
    }

    private void OnSessionEnded()
    {
        Weapons.ResetToggles();
        Cards.SaveCache();
        // Only the in-flight bookkeeping: object ids do not survive a session, so pending shots
        // and per-attacker clocks would resolve against strangers. The per-class totals do
        // survive, because a class is the same class next time.
        Fights.Clear();
        lock (_gate) { _target = 0; _lockedTarget = 0; _subscribedTarget = 0; }
    }

    /// <summary>
    /// A WhoIs named a model. Ask for its card while it is still far away.
    ///
    /// Two rules, both learned the hard way:
    ///
    /// <b>Only ships get a Ship view.</b> Asking for a view a guid does not have is not a
    /// harmless miss — the server logs its own error and sends nothing, which reads to us as
    /// silence and gets retried. Mines were in this list and should never have been: a mine has
    /// a World card like everything else, and no Ship card at all.
    ///
    /// <b>World only for things that shoot at us</b>, which is why asteroids and planetoids are
    /// absent despite being the things we actually fly around. Their radius already arrives in
    /// the WhoIs body and is read straight into <c>SpaceObj.Radius</c>, so a card would buy
    /// nothing — and there are hundreds of rocks in a belt against a handful of hostiles, so
    /// asking for all of them is hundreds of requests the real client also has to swallow.
    /// </summary>
    private void OnObjectIdentified(uint objectId, uint cardGuid, SpaceEntityType type)
    {
        if (!T.FetchCatalogue) return;

        bool isShip = EntityTypes.IsShip(objectId);
        bool worthKnowing = isShip
            || type is SpaceEntityType.Mine or SpaceEntityType.SmartMine or SpaceEntityType.MineField;
        if (!worthKnowing) return;

        if (isShip) Cards.Want(cardGuid, CardView.Ship);
        Cards.Want(cardGuid, CardView.World);
    }

    private void OnSectorLeft(RemovingCause cause)
    {
        Weapons.ResetToggles();
        ForgetThrottle();

        // A fresh spell out of the sector: the relaunch sequence starts from here, whatever the
        // last one did. Note what is NOT reset — the death screen and the fact that we died. The
        // two messages arrive in whichever order the server sends them, and wiping a respawn
        // offer that landed first would leave the ship dead with nothing left to answer it.
        _hangarSince = DateTime.UtcNow;
        _launchAsks = 0;
        _lastLaunchAsk = DateTime.MinValue;
        _respawnAnswered = DateTime.MinValue;
        _repairAsked = false;
        _repairWarned = false;
        _conditionBeforeRepair = null;

        if (cause == RemovingCause.Death)
        {
            Deaths++;
            _diedHere = true;
            Log?.Invoke(T.AutoUndock
                ? $"Destroyed (death #{Deaths}). Waiting for the respawn options."
                : $"Destroyed (death #{Deaths}). Auto undock is off — respawn in the client.");
        }

        // You parked the ship yourself. Relaunching it would be the bot undoing an instruction,
        // so the farm stops instead and says why.
        bool yourDock = cause == RemovingCause.Dock
                     && !_docking
                     && (DateTime.UtcNow - _youDockedAt).TotalSeconds < 60;
        if (yourDock && Enabled)
        {
            Stop();
            Log?.Invoke("You docked by hand — farming stopped. Press Go farm after undocking.");
        }

        // Leaving by Dock means the dock run worked; anything else ends it just as surely.
        if (_docking)
        {
            _docking = false;
            _dockTarget = 0;
            Status = cause == RemovingCause.Dock ? "Docked" : $"Dock run ended ({cause})";
            if (!Enabled) _timer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        // A destination in a sector you are no longer in is not a destination.
        if (_following) EndFollow($"Fly-to ended — you left the sector ({cause})");
        ForgetSectorState();
        Log?.Invoke($"Left the sector ({cause}). World cleared.");
    }

    /// <summary>
    /// Drops everything the bot believes about a specific object id.
    ///
    /// <para>Every collection in here is keyed by object id, and <b>object ids are re-used from
    /// one sector to the next</b> — the type lives in the id's top bits and the rest is an index
    /// the server hands out per sector. So a verdict reached about rock #07000019 in one sector
    /// lands on a completely unrelated rock in the next one.</para>
    ///
    /// <para><see cref="_skip"/> and <see cref="_hardSkip"/> are the ones that hurt, and they are
    /// exactly the two this used to leave behind. A rock condemned as unscannable is muted for
    /// <see cref="BotTuning.MuteRockSkipMinutes"/> — thirty minutes — so after a jump the bot
    /// arrives already refusing a handful of perfectly good rocks it has never seen, skips the
    /// scanned and confirmed ones among them, and goes looking for something else. That is the
    /// "it ignores rocks it just confirmed and chases ghosts after a jump" behaviour.</para>
    ///
    /// <para>What deliberately does NOT reset: <see cref="_probed"/> and the weapon book. Which
    /// ability is your scanner is a fact about your ship, not about a sector.</para>
    /// </summary>
    private void ForgetSectorState()
    {
        lock (_gate)
        {
            _target = 0; _lockedTarget = 0; _subscribedTarget = 0; _pinned = 0;
            _lootAsked.Clear(); _facilityOrdered.Clear();
            // Rocks are per-sector; the learned scanner is not, so _probed stays.
            _scanAsked.Clear(); _scanProbe.Clear();
            _scanStrikes.Clear(); _unansweredRocks.Clear();
            // The scanner-is-dead verdict goes with them. Every silent cast it was built on
            // aimed at a rock that no longer exists for us, and a hangar visit is exactly
            // where empty power cells get refilled — a verdict carried through a dock once
            // condemned a working scanner two casts into the next launch.
            _scansWithoutReply = 0;
            _ammoWarned = false;
            _filterAbandoned = false;
            // A station that would not take us in is a fact about that station, and ids do not
            // survive a sector change.
            _dockRefused.Clear();
            // The condemned-rock lists. See the note above: leaving these behind is what makes a
            // fresh sector look like one the bot has already given up on.
            _skip.Clear(); _hardSkip.Clear();
            // Which bodies we have asked the size of. Different sector, different bodies.
            _sizeAsked.Clear();
        }

        // In-flight bookkeeping that names an id. None of these survive the objects they refer
        // to, and a watchdog still counting against a rock in the last sector fires on the next.
        _mineWatchId = 0;
        _holdId = 0;
        _roamTarget = 0;
        _approachId = 0;
        _dodgeId = 0; _dodgeSince = DateTime.MinValue;
        _escapeFrom = 0; _escapeSince = DateTime.MinValue;
        _detourSince = DateTime.MinValue;
    }

    /// <summary>Watches both directions: your traffic teaches the bot, the server's builds the map.</summary>
    private void OnFrame(FrameInfo f)
    {
        try
        {
            var r = f.Reader();

            if (!f.FromClient)
            {
                // Cards the client asked for pass through here too, so the catalogue fills in
                // from the client's own browsing before we request anything ourselves.
                if (f.Protocol == ProtocolId.Catalogue
                    && (CatalogueOp.Reply)f.MsgType == CatalogueOp.Reply.Card)
                {
                    Cards.OnCardReply(r);
                    return;
                }

                // The docking countdown. The server answers a dock request with the delay it is
                // imposing — the client disables its DOCK button for that long and offers
                // CancelDocking instead (DockingButton.UpdateState). The bot knew the opcode and
                // did nothing with it, so it could not tell a countdown from silence.
                //
                // Read through its own reader: `r` is handed to the world model below, and a
                // half-consumed one would leave that reading from the middle of the message.
                if (f.Protocol == ProtocolId.Game
                    && (GameOp.Reply)f.MsgType == GameOp.Reply.DockingDelay)
                    NoteDockingDelay(f.Reader().ReadSingle());

                _world.OnServerMessage(f.Protocol, f.MsgType, r);
                return;
            }

            switch (f.Protocol)
            {
                case ProtocolId.Login when (LoginOp.Request)f.MsgType == LoginOp.Request.Player:
                {
                    r.ReadByte();                                  // ConnectType
                    _world.SeedPlayerId(r.ReadUInt32(), "your login");
                    break;
                }

                case ProtocolId.Game:
                    OnClientGameMessage(f, r);
                    break;

                // What the hangar buttons actually send, printed as it happens. This is how the
                // undock sequence was pinned down rather than guessed: press UNDOCK and the log
                // states the message, in order, with nothing inferred.
                case ProtocolId.Room:
                    Log?.Invoke($"Client sent Room/{(RoomOp.Request)f.MsgType} ({f.MsgType}).");

                    // Room.Enter is the client loading a hangar, which it only does once the
                    // server has docked it. The server does not always announce that dock to us —
                    // one retreat docked cleanly, got no RemoveMe, and the bot spent the next
                    // half minute circling a station it was already inside of, steering by a
                    // position frozen at the moment of the dock. The client's own room load is
                    // the one signal that cannot be missing, so if the world still thinks we are
                    // flying when it lands, this IS the dock notification.
                    if ((RoomOp.Request)f.MsgType == RoomOp.Request.Enter
                        && (_world.MyObjectId != 0 || _world.MyPositionKnown))
                    {
                        Log?.Invoke("Room/Enter with the ship still in the world — the dock "
                                  + "succeeded without a RemoveMe. Treating it as docked.");
                        _world.Clear();
                        OnSectorLeft(RemovingCause.Dock);
                    }
                    break;
            }
        }
        catch
        {
            // Short or unfamiliar payloads are normal — never let parsing break the relay.
        }
    }

    private void OnClientGameMessage(FrameInfo f, BgoReader r)
    {
        NoteClientMessage(f.MsgType);

        switch ((GameOp.Request)f.MsgType)
        {
            // The three ways the client fires. Only watching CastSlotAbility meant a beam or
            // any auto-cast weapon never registered, no matter how long you held the trigger.
            // What you aimed at is read too: an ability pointed at a rock is a mining laser,
            // one pointed at a ship is a gun. Assuming "combat" for everything you fired is
            // what left mining mode forever asking you to fire your laser once manually.
            case GameOp.Request.CastSlotAbility:
            case GameOp.Request.CastImmutableSlotAbility:
            {
                ushort id = r.ReadUInt16();
                var targets = ReadTargets(r);
                Weapons.Observe(id, WeaponKind.Cast, RoleOf(targets));
                LearnAreaEffect(id, targets);
                NoteScanProbe(id, targets);
                AbilitySeen?.Invoke(id);
                break;
            }

            case GameOp.Request.ToggleAbilityOn:
            case GameOp.Request.UpdateAbilityTargets:
            {
                ushort id = r.ReadUInt16();
                var targets = ReadTargets(r);
                Weapons.Observe(id, WeaponKind.Toggle, RoleOf(targets));
                LearnAreaEffect(id, targets);
                NoteScanProbe(id, targets);
                AbilitySeen?.Invoke(id);
                break;
            }

            // The client has already turned Full/Delta into an absolute number by the time it
            // gets here, so your own throttle is a server-independent source for our top speed.
            case GameOp.Request.SetSpeed:
            {
                r.ReadByte();                                  // SpeedMode — no server reads it
                float v = r.ReadSingle();
                // Only worth a line when it actually moves the number we fly at — otherwise
                // every tap of your throttle key logged a "learned" speed that changed nothing.
                if (v > _observedTopSpeed)
                {
                    float before = TopSpeed;
                    _observedTopSpeed = v;
                    if (TopSpeed > before)
                        Log?.Invoke($"Watched you fly at {v:F0}u/s — using that as the top speed.");
                }
                break;
            }

            case GameOp.Request.ToggleAbilityOff:
            {
                ushort id = r.ReadUInt16();
                var w = Weapons.Find(id);
                if (w is not null) { w.ToggledOn = false; w.ToggleTarget = 0; }
                break;
            }

            // The other half of the undock sequence, logged for the same reason as Room.Quit:
            // the client sends this itself once the space level has loaded, and seeing the two
            // land in order is the whole proof of how undocking works.
            // Also the moment to forget the last sector, and the ONLY one that can be relied on.
            //
            // OnSectorLeft runs off Reply.RemoveMe, which the server does not always send us:
            // jumping by hand, and respawning into a sector other than the one you died in, both
            // put the ship somewhere new without that message necessarily arriving. JumpIn is the
            // client stating its space level has loaded, so whatever happened, the objects around
            // us now are new ones — and any id-keyed verdict we still hold describes a different
            // sector's objects. Clearing twice costs nothing; not clearing costs half an hour of
            // skipping good rocks.
            // The world map has to go too, not just the verdicts. Without RemoveMe nothing ever
            // removed the old sector's objects from WorldState, so every rock from the last
            // sector kept its id and position on the map, the new sector's objects merged in on
            // top, and the bot flew to asteroids that are a sector away — the "ghost rocks".
            // Clearing here is safe on ordering: the client sends JumpIn from inside the space
            // level's loading screen (SpaceLevel.PreloadLevel) and the server streams the new
            // sector's WhoIs only after it, so nothing real is thrown away.
            case GameOp.Request.JumpIn:
                _world.Clear();
                ForgetSectorState();
                Log?.Invoke("Client sent Game/JumpIn (61) — its space level has finished loading. "
                          + "Dropped the old sector's map and verdicts.");
                break;

            // You picked a target by hand — respect it if it suits the current mode.
            case GameOp.Request.LockTarget:
            {
                uint id = r.ReadUInt32();
                AdoptManualTarget(id);
                break;
            }

            // You asked to mine something by hand — take that as the mining target.
            case GameOp.Request.Mining:
                AdoptManualTarget(r.ReadUInt32());
                break;

            // You docked by hand. The distance you did it from is a proven-good dock range for
            // that station, which beats any number we could invent.
            case GameOp.Request.Dock:
            {
                uint id = r.ReadUInt32();

                // Only the real client's traffic reaches here — injected frames go straight to
                // the server — so this really is you pressing dock, not the bot's own dock run.
                _youDockedAt = DateTime.UtcNow;

                // Dumped raw, because every Dock the bot has sent itself was followed within
                // 400ms by the server hanging up, three times out of three, while the message
                // itself is byte-for-byte what GameProtocol.RequestDock writes. Something ELSE
                // about a real dock differs, and the only way to find out what is to read one.
                //
                // The whole frame, not just this message: the client batches everything queued in
                // a tick into one frame, so if a dock is really "LockTarget then Dock" — which is
                // what SpaceLevel.Dock implies, since it docks GetPlayerTarget() — the proof is
                // in the other messages sitting beside it.
                DumpDockFrame(f);

                var station = _world.Get(id);
                if (station is not null && _world.DistanceToMe(station) is { } d && d > _learnedDockRange)
                {
                    _learnedDockRange = d;
                    Log?.Invoke($"Learned a working dock range: {d:F0}u (from your own docking).");
                }
                break;
            }
        }
    }

    /// <summary>The id list that follows an ability id in every cast and toggle message.</summary>
    private static List<uint> ReadTargets(BgoReader r)
    {
        var ids = new List<uint>(1);
        try
        {
            int n = r.ReadUInt16();
            for (int i = 0; i < n; i++) ids.Add(r.ReadUInt32());
        }
        catch
        {
            // Truncated list — whatever we got is still usable.
        }
        return ids;
    }

    /// <summary>
    /// What an ability is for, judged by what you aimed it at. Weak evidence only: the
    /// per-slot stat stream is authoritative and overwrites this.
    /// </summary>
    private WeaponRole RoleOf(List<uint> targets)
    {
        foreach (uint id in targets)
        {
            // Your own ship first. Damage Control and every other self-cast targets you, and you
            // are ship-shaped — so this used to come back Combat, and the bot would happily try
            // to shoot an NPC with your repair module.
            if (id != 0 && id == _world.MyObjectId) return WeaponRole.Repair;
            if (EntityTypes.IsMinable(id)) return WeaponRole.Mining;
            if (EntityTypes.IsShip(id)) return WeaponRole.Combat;
        }
        return WeaponRole.Unknown;
    }

    /// <summary>
    /// Remembers that you pointed an ability at a rock. If the server answers with a scan for
    /// that same rock in the next few seconds, that ability was the scanner — which is the only
    /// way to find it without parsing the catalogue, since nothing else names it on the wire.
    /// </summary>
    /// <summary>
    /// Works out whether an ability is area-effect purely from watching you use it, because the
    /// consequence of getting it wrong is a cheat entry in the server log.
    ///
    /// The client's own rule makes this decidable: an Area cast carries EVERY valid object in
    /// range, a Selected cast carries exactly one. So more than one id proves Area outright, and
    /// exactly one id while several valid targets were in range proves Selected.
    /// </summary>
    private void LearnAreaEffect(ushort abilityId, List<uint> targets)
    {
        var w = Weapons.Find(abilityId);
        if (w is null || w.Area is not null) return;

        if (targets.Count > 1)
        {
            w.Area = true;
            Log?.Invoke($"Ability #{abilityId} is area-effect ({targets.Count} targets in one cast).");
            return;
        }

        if (targets.Count != 1) return;

        float reach = _world.SlotStat(abilityId, ObjectStat.MaxRange) ?? w.MaxRange ?? 0f;
        if (reach <= 0f) return;

        var now = DateTime.UtcNow;
        int inRange = _world.Snapshot().Count(o => EntityTypes.IsMinable(o.Id)
                                               && o.HasPosition
                                               && (_world.DistanceToMe(o) ?? float.MaxValue) <= reach);
        if (inRange >= 2)
        {
            w.Area = false;
            Log?.Invoke($"Ability #{abilityId} is single-target ({inRange} rocks were in reach, it took one).");
        }
    }

    private void NoteScanProbe(ushort abilityId, List<uint> targets)
    {
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            foreach (uint id in targets)
                if (EntityTypes.IsMinable(id)) _scanProbe[id] = (abilityId, now);

            // Never let this grow: anything older than the correlation window is dead weight.
            if (_scanProbe.Count > 64)
                foreach (var stale in _scanProbe.Where(p => (now - p.Value.At).TotalSeconds > 10).Select(p => p.Key).ToList())
                    _scanProbe.Remove(stale);
        }
    }

    private void OnScanReceived(uint asteroidId)
    {
        // Any reply at all proves the scanner is fed and firing, whoever triggered it.
        _scansWithoutReply = 0;
        lock (_gate) { _scanStrikes.Remove(asteroidId); _unansweredRocks.Clear(); }
        if (_filterAbandoned) { _filterAbandoned = false; Log?.Invoke("Scanner is answering again — resource filtering is back on."); }

        // An answer is proof of reach: the server drops scan targets outside the ability's own
        // MaxRange without saying anything, so anything it DID answer was inside it. This is the
        // only honest measurement of a reach nothing publishes — and it grows on its own, so a
        // scanner that turns out to reach 2,000u is used at 2,000u without anyone typing that in.
        if (_world.Get(asteroidId) is { } rock && _world.DistanceToMe(rock) is { } d
            && d > _scanProvenRange)
        {
            float was = _scanProvenRange;
            _scanProvenRange = d;
            // Worded as the floor it is. "Using that as the reach" read as a measurement of the
            // scanner when it is only the furthest we happen to have proved so far — and it is
            // printed at all ONLY when the published range has gone missing, so the line is really
            // a symptom of that. Say so, because the cure is upstream.
            if (Scanner()?.MaxRange is not > 0 && d > was * 1.2f)
                Log?.Invoke($"Scan answered from {d:F0}u, so the scanner reaches at least that far "
                          + "— no published range for it, so that floor is all there is to go on. "
                          + "Declare its range in the loadout panel if you know it.");
        }

        ushort ability;
        lock (_gate)
        {
            if (!_scanProbe.TryGetValue(asteroidId, out var probe)) return;
            if ((DateTime.UtcNow - probe.At).TotalSeconds > 5) { _scanProbe.Remove(asteroidId); return; }
            _scanProbe.Remove(asteroidId);
            ability = probe.Ability;
        }

        var w = Weapons.MarkScanner(ability);
        if (w is not null)
            Log?.Invoke($"Learned your resource scanner: ability #{ability}. Mining can filter by resource now.");
    }

    // ------------------------------------------------------------------ picked by hand

    /// <summary>
    /// Holds one contact as the target until it dies, leaves the sector, or you clear it.
    ///
    /// Distinct from the target the bot picks: that one is re-derived every tick from whatever
    /// is nearest and eligible, so writing to it would last exactly one tick. A pin is checked
    /// before the hunting rules and survives them — including the "attack players" and prey
    /// filters, because pointing at something explicitly is a clearer instruction than any
    /// checkbox.
    /// </summary>
    public void Pin(uint id)
    {
        if (id == 0 || id == _world.MyObjectId) return;
        lock (_gate)
        {
            _pinned = id;
            _target = id;
            _lockedTarget = 0;              // force a fresh LockTarget on the next tick
            _skip.Remove(id);
        }

        var o = _world.Get(id);
        bool suits = T.Mode == FarmMode.Mining ? EntityTypes.IsMinable(id) : !EntityTypes.IsMinable(id);
        Log?.Invoke($"Pinned {o?.ToString() ?? $"#{id:X8}"} as the target."
                  + (suits ? "" : $" It is not a {T.Mode} target, so switch mode or it will be dropped."));
    }

    public void Unpin()
    {
        bool had;
        lock (_gate) { had = _pinned != 0; _pinned = 0; }
        if (had) Log?.Invoke("Pin cleared — back to picking targets automatically.");
    }

    /// <summary>
    /// Fires one ability once, so you can see in game which slot an id belongs to.
    ///
    /// Aimed at whatever is locked, falling back to your own ship: a repair module cast at a
    /// rock is refused, and a gun cast at yourself is refused, but between the two every slot
    /// gets a shot at showing itself. The server may well reject it — that is fine, the point
    /// is the visible cooldown sweep on the hex in the real client.
    /// </summary>
    public async Task TestFireAsync(ushort abilityId)
    {
        uint at;
        lock (_gate) at = _target;
        if (at == 0) at = _world.MyObjectId;

        var w = Weapons.Find(abilityId);
        string aimed = at == _world.MyObjectId ? "at your own ship" : $"at #{at:X8}";
        try
        {
            if (w?.Kind == WeaponKind.Toggle) await _act.ToggleAbilityOn(abilityId, at);
            else await _act.CastSlotAbility(abilityId, at);
            Log?.Invoke($"Test fired ability #{abilityId} {aimed} — watch which hex lights up in game.");
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Could not test fire #{abilityId}: {ex.Message}");
        }
    }

    private void AdoptManualTarget(uint id)
    {
        if (id == 0 || id == _world.MyObjectId) return;
        bool suits = T.Mode == FarmMode.Mining ? EntityTypes.IsMinable(id) : EntityTypes.IsShip(id);
        if (!suits) return;

        lock (_gate)
        {
            if (_target == id) return;
            _target = id;
            _lockedTarget = id;      // the client just sent the lock; no need to repeat it
            _skip.Remove(id);
        }
        Log?.Invoke($"Following your manual target #{id:X8}.");
    }

    private void OnObjectDied(uint id)
    {
        bool wasTarget;
        lock (_gate)
        {
            wasTarget = id == _target;
            if (wasTarget) { _target = 0; _lockedTarget = 0; }
            if (id == _pinned) _pinned = 0;
            _skip.Remove(id);
        }

        if (!wasTarget) return;

        Kills++;
        Log?.Invoke($"Target #{id:X8} destroyed (kill {Kills}).");
        _ = StopAllTogglesAsync();
        if (T.AutoLoot) _ = TryLootAsync(id);
    }

    private void OnCastResult(ushort slot, bool ok)
    {
        if (ok) return;
        Rejections++;
        // Rate-limited: a rejected cast every tick would flood the log.
        if (Rejections % 20 == 1)
            Log?.Invoke($"Server rejected ability #{slot} (out of range, no power, or on cooldown).");
    }

    private void OnAbilityStopped(short slot)
    {
        if (slot < 0) { Weapons.ResetToggles(); return; }
        var w = Weapons.Find((ushort)slot);
        if (w is not null) { w.ToggledOn = false; w.ToggleTarget = 0; }
    }

    private void OnLootOffered(ushort lootId, IReadOnlyList<LootItem> items)
    {
        if (!T.AutoLoot || items.Count == 0) return;
        var ids = items.Select(i => i.ServerId).ToList();
        _ = _act.TakeLootItems(lootId, ids);
        LootTaken += ids.Count;
        Log?.Invoke($"Taking {ids.Count} item(s) from loot #{lootId}.");
    }

}
