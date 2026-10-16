using CH.Protocol;
using CH.Protocol.Messages;
using CH.Server.Game;
using Lidgren.Network;
using Microsoft.Extensions.Logging;

namespace CH.Server;

/// <summary>
/// Other players (docs/multiplayer-notes.md): who is in the zone, where they are, what they wear, their fights, emotes,
/// whispers and parties. Rules the client imposes: never send a client a ZoningIn for a player it already has (it uses
/// Dictionary.Add) or for itself; position batches are full snapshots; other players' health is a percentage.
/// </summary>
public sealed partial class GameServer
{
    private const double PositionBatchSeconds = 0.2;
    private const double IdleBatchSeconds = 1.0;
    private const double MovingWindowSeconds = 2.0;

    /// <summary>Party limit and XP sharing distance. <b>Approximated.</b></summary>
    private const int MaxPartySize = 6;
    private const float PartyShareRange = 60f;

    private sealed class Party
    {
        public List<Session> Members { get; } = new();
    }

    private static IEnumerable<Session> Others(Zone zone, Session s) => zone.Players.Where(p => !ReferenceEquals(p, s) && p.InZone);

    private void SendToOthers(Zone zone, Session s, Opcode opcode, Action<NetBuffer> write)
    {
        foreach (var p in Others(zone, s).ToList()) Send(p, opcode, write);
    }

    private OtherPlayer Basic(Session p) =>
        new(p.Sheet!.Summary(_options.WorldId) with { Zone = p.Position.Zone }, p.Position.X, p.Position.Y, p.Position.Z, p.Position.Direction);

    /// <summary>Health as other clients see it: 0-100, never 0 while alive.</summary>
    private static int HealthPercent(Session p) =>
        p.Health <= 0 ? 0 : Math.Clamp((int)Math.Ceiling(p.Health * 100.0 / Math.Max(1, p.MaxHealth)), 1, 100);

    private IEnumerable<Session> Online => _sessions.Values.Where(p => p.Character is not null && p.Sheet is not null);

    private Session? OnlineByCharacter(int characterId) => Online.FirstOrDefault(p => p.CharacterId == characterId);

    private Session? OnlineByName(string name) =>
        Online.FirstOrDefault(p => p.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));

    // ---- presence and movement --------------------------------------------------------------

    /// <summary>Called by EnterZone after the joining client got the snapshot: tell everyone else.</summary>
    private void AnnounceArrival(Zone zone, Session s, bool wasPresent)
    {
        if (wasPresent) return; // an unsolicited FinishedZoning (graphics reload): the others already know this player
        var basic = Basic(s);
        SendToOthers(zone, s, Opcode.CharacterZoningIn, basic.Write);
        zone.NextBatchAt = 0; // place the newcomer on everyone's screen right away
        var now = NetTime.Now;
        foreach (var other in Others(zone, s).ToList())
        {
            if (s.Effects.Count > 0) SendEffectsOf(s, other, now);
            if (other.Effects.Count > 0) SendEffectsOf(other, s, now);
        }
    }

    private void AnnounceDeparture(Zone zone, Session s) =>
        SendToOthers(zone, s, Opcode.CharacterZoningOut, b => b.WriteVariableUInt32((uint)s.CharacterId));

    /// <summary>Full position snapshots of the other players, 5 Hz while anyone moves, 1 Hz otherwise.</summary>
    private void TickPositions(Zone zone, double now)
    {
        if (now < zone.NextBatchAt) return;
        var present = zone.Players.Where(p => p.InZone).ToList();
        if (present.Count < 2)
        {
            zone.NextBatchAt = now + IdleBatchSeconds;
            return;
        }
        var moving = present.Any(p => now - p.LastMoveAt < MovingWindowSeconds);
        zone.NextBatchAt = now + (moving ? PositionBatchSeconds : IdleBatchSeconds);
        var all = present.Select(p => new PlayerPosition((uint)p.CharacterId, p.Position.X, p.Position.Y, p.Position.Z, p.Position.Direction,
            p.MoveSpeed <= 0 || now - p.LastMoveAt > MovingWindowSeconds)).ToList();
        foreach (var recipient in present)
        {
            var hidden = present.Where(p => !ReferenceEquals(p, recipient) && IsHiddenFromPlayer(p, recipient)).Select(p => (uint)p.CharacterId).ToHashSet();
            var batch = new BatchZonePositionUpdate(now, all.Where(e => e.ServerId != (uint)recipient.CharacterId && !hidden.Contains(e.ServerId)).ToList());
            Send(recipient, Opcode.BatchZonePositionUpdate, batch.Write, delivery: NetDeliveryMethod.UnreliableSequenced);
        }
    }

    /// <summary>Gear, display options or level changed: redraw this player for everyone else in the zone.</summary>
    private void BroadcastAppearance(Session s)
    {
        if (!s.InZone || !_zones.TryGetValue(s.Position.Zone, out var zone) || s.Sheet is null) return;
        var summary = s.Sheet.Summary(_options.WorldId);
        var progress = s.Sheet.Progress;
        SendToOthers(zone, s, Opcode.PlayerAppearanceUpdate, b => PlayerAppearanceUpdate.Write(b, (uint)s.CharacterId, summary.Equipment,
            progress.ShowHeadgear, progress.ShowFashion, s.Sheet.Level, s.Sheet.PvpLevel, s.Sheet.PvpRating));
    }

    /// <summary>The same character logging in again (a second client): the old session is dropped.</summary>
    private void KickDuplicate(Session s, long characterId)
    {
        foreach (var old in _sessions.Values.Where(o => !ReferenceEquals(o, s) && o.Character?.Id == characterId).ToList())
        {
            if (old.InZone) LeaveZone(old);
            LeaveParty(old);
            old.Character = null;
            old.Connection.Disconnect("This character logged in from another client.");
            _log.LogInformation("Dropped the older session of character {Id}", characterId);
        }
    }

    /// <summary>
    /// Hide makes a player invisible to other players (<b>approximated</b>: party members still see them). Hidden
    /// players are left out of the viewer's position snapshots, which is what hides a model on the client.
    /// </summary>
    private bool IsHiddenFromPlayer(Session p, Session viewer) =>
        Modifiers(p).HiddenUpToLevel > 0 && (p.Party is null || !ReferenceEquals(p.Party, viewer.Party));

    /// <summary>
    /// OthersStatusEffect (15018): <c>varint n × {byte isMob, varuint id, status list}</c> for players the viewer sees,
    /// then the same for players hidden from it (the client marks those invisible).
    /// </summary>
    private void SendEffectsOf(Session p, Session viewer, double now)
    {
        var hidden = IsHiddenFromPlayer(p, viewer);
        Send(viewer, Opcode.OthersStatusEffect, b =>
        {
            b.WriteVariableInt32(hidden ? 0 : 1);
            if (!hidden) WriteOther(b);
            b.WriteVariableInt32(hidden ? 1 : 0);
            if (hidden) WriteOther(b);
        });

        void WriteOther(NetBuffer b)
        {
            b.Write((byte)0);
            b.WriteVariableUInt32((uint)p.CharacterId);
            WriteEffectList(b, p, now);
        }
    }

    /// <summary>A player's effects changed: everyone else in the zone sees the new buffs (and whether they're hidden).</summary>
    private void BroadcastEffects(Session s)
    {
        if (!s.InZone || !_zones.TryGetValue(s.Position.Zone, out var zone)) return;
        var now = NetTime.Now;
        foreach (var viewer in Others(zone, s).ToList()) SendEffectsOf(s, viewer, now);
        zone.NextBatchAt = 0; // a hidden/unhidden player appears or vanishes with the next snapshot
    }

    /// <summary>Health regen and other non-combat changes: other players' health bars (percent) follow.</summary>
    private void ShareHealth(Zone zone, Session s)
    {
        var percent = HealthPercent(s);
        if (percent == s.SharedHealthPercent) return;
        s.SharedHealthPercent = percent;
        SendToOthers(zone, s, Opcode.CombatDamageMessage, new CombatDamage([], [new HealthUpdate(HealthUpdate.Player, s.CharacterId, percent)]).Write);
    }

    // ---- combat seen by others ---------------------------------------------------------------

    /// <summary>Sets a player's auto-attack target; others see the swing animation start and stop.</summary>
    private void SetAttackTarget(Session s, Mob? target)
    {
        var old = s.AttackTarget;
        if (ReferenceEquals(old, target)) return;
        s.AttackTarget = target;
        if (!s.InZone || !_zones.TryGetValue(s.Position.Zone, out var zone)) return;
        if (old is not null)
            SendToOthers(zone, s, Opcode.AttackState, new AttackState(false, false, s.CharacterId, true, (int)old.ServerId).Write);
        if (target is not null)
            SendToOthers(zone, s, Opcode.AttackState, new AttackState(true, false, s.CharacterId, true, (int)target.ServerId).Write);
    }

    /// <summary>A damage or heal entry on a player: absolute health for that player, a percentage for everyone else.</summary>
    private void SendPlayerDamage(Zone zone, Session victim, DamageEntry entry)
    {
        Send(victim, Opcode.CombatDamageMessage,
            new CombatDamage([entry], [new HealthUpdate(HealthUpdate.Player, victim.CharacterId, victim.Health)]).Write);
        var percent = HealthPercent(victim);
        victim.SharedHealthPercent = percent;
        var shared = new CombatDamage([entry with { TargetHealth = percent }], [new HealthUpdate(HealthUpdate.Player, victim.CharacterId, percent)]);
        SendToOthers(zone, victim, Opcode.CombatDamageMessage, shared.Write);
    }

    // ---- emotes and chat ----------------------------------------------------------------------

    private void OnEmote(Session s, EmoteRequest req)
    {
        if (!s.InZone || !_zones.TryGetValue(s.Position.Zone, out var zone)) return;
        // The sender's own client waits for the echo before playing it.
        Broadcast(zone, Opcode.CharacterPlayingEmote, b => EmoteRequest.WriteToClient(b, (uint)s.CharacterId, req.Emote, req.Loop));
    }

    /// <summary>Whisper text is "w Name, message" (CInGameTextBox).</summary>
    private void Whisper(Session s, string text)
    {
        var comma = text.IndexOf(',');
        if (comma < 0)
        {
            Tell(s, "Whisper as: w Name, message");
            return;
        }
        var targetName = text[..comma].Trim();
        var message = text[(comma + 1)..].Trim();
        if (OnlineByName(targetName) is not { } target || IsBlockedBy(s, target))
        {
            Tell(s, $"{targetName} is not online.");
            return;
        }
        Send(target, Opcode.GeneralChat, new ChatMessage(ChatChannel.WhisperIncoming, s.Name, message).WriteToClient);
        Send(s, Opcode.GeneralChat, new ChatMessage(ChatChannel.WhisperOutgoing, target.Name, message).WriteToClient);
    }

    private void PartyChat(Session s, string text)
    {
        if (s.Party is null)
        {
            Tell(s, "You are not in a party.");
            return;
        }
        foreach (var m in s.Party.Members.ToList()) Send(m, Opcode.GeneralChat, new ChatMessage(ChatChannel.Group, s.Name, text).WriteToClient);
    }

    private void WorldChat(Session s, ChatChannel channel, string text)
    {
        foreach (var p in Online.Where(p => !IsBlockedBy(s, p)).ToList()) Send(p, Opcode.GeneralChat, new ChatMessage(channel, s.Name, text).WriteToClient);
    }

    // ---- parties ---------------------------------------------------------------------------------

    private void OnPartyMessage(Session s, NetIncomingMessage msg)
    {
        switch ((PartyMessageType)msg.ReadVariableInt32())
        {
            case PartyMessageType.RequestFromPlayer:
                InviteToParty(s, msg.ReadVariableInt32());
                break;
            case PartyMessageType.ReplyFromPlayer:
                var requester = msg.ReadVariableInt32();
                AnswerPartyInvite(s, requester, (PartyReply)msg.ReadVariableInt32());
                break;
            case PartyMessageType.Leave:
                LeaveParty(s);
                break;
        }
    }

    private void InviteToParty(Session s, int inviteeId)
    {
        var invitee = OnlineByCharacter(inviteeId);
        // The invitee's client shows the prompt only for a player it can see, so both must be in the same zone.
        string? problem = invitee is null || IsBlockedBy(s, invitee) ? "That player is not online."
            : ReferenceEquals(invitee, s) ? "You can't invite yourself."
            : !invitee.InZone || invitee.Position.Zone != s.Position.Zone ? $"{invitee.Name} must be in the same zone to be invited."
            : invitee.Party is not null && ReferenceEquals(invitee.Party, s.Party) ? $"{invitee.Name} is already in your party."
            : invitee.Party is not null ? $"{invitee.Name} is already in a party."
            : s.Party is { } p && p.Members.Count >= MaxPartySize ? "Your party is full."
            : null;
        if (problem is not null)
        {
            Tell(s, problem);
            return;
        }
        invitee!.PartyInvites.Add(s.CharacterId);
        Send(invitee, Opcode.PartyMessage, b => PartyMessage.WriteRequest(b, s.CharacterId));
    }

    private void AnswerPartyInvite(Session s, int requesterId, PartyReply reply)
    {
        if (!s.PartyInvites.Remove(requesterId) || OnlineByCharacter(requesterId) is not { } requester) return;
        Send(requester, Opcode.PartyMessage, b => PartyMessage.WriteReply(b, s.CharacterId, reply));
        if (reply != PartyReply.Accept) return;
        var party = requester.Party;
        if (party is null)
        {
            party = new Party();
            party.Members.Add(requester);
            requester.Party = party;
        }
        if (party.Members.Count >= MaxPartySize)
        {
            Tell(s, "That party is full.");
            return;
        }
        if (s.Party is not null) LeaveParty(s);
        party.Members.Add(s);
        s.Party = party;
        SendRoster(party);
        RefreshZoneHate(s.Position.Zone);
        _log.LogInformation("{Name} joined {Leader}'s party ({Count} members)", s.Name, requester.Name, party.Members.Count);
    }

    private void LeaveParty(Session s)
    {
        if (s.Party is not { } party) return;
        party.Members.Remove(s);
        s.Party = null;
        RefreshZoneHate(s.Position.Zone);
        if (s.Character is not null) Send(s, Opcode.PartyMessage, b => PartyMessage.WriteRoster(b, []));
        if (party.Members.Count == 1)
        {
            var last = party.Members[0];
            party.Members.Clear();
            last.Party = null;
            Send(last, Opcode.PartyMessage, b => PartyMessage.WriteRoster(b, []));
        }
        else if (party.Members.Count > 1)
        {
            SendRoster(party);
        }
    }

    /// <summary>Everyone, the recipient included (the client skips its own entry).</summary>
    private void SendRoster(Party party)
    {
        var roster = party.Members.Select(m => new PartyMember(m.Name, m.CharacterId, m.Position.Zone, m.Sheet?.Level ?? 1, m.Character?.Class ?? 0)).ToList();
        foreach (var m in party.Members.ToList()) Send(m, Opcode.PartyMessage, b => PartyMessage.WriteRoster(b, roster));
    }

    /// <summary>
    /// Kill XP (and quest credit) for party members near the killer: each gets the full amount (<b>approximated</b>; the live split is
    /// unknown). Loot and coins stay with the killer.
    /// </summary>
    /// <summary>
    /// Who gets each of a kill's drops (owner's rule, 2026-10-16). Alone, the killer gets them all. In a party, the killer
    /// and every member in the zone, alive and within <see cref="PartyShareRange"/>, take turns in a random order: the
    /// shuffled drops go round them one at a time, so everyone gets one before anyone gets a second, and the extras land
    /// on random members.
    /// </summary>
    private List<(Session Owner, int Item, int Quantity)> DealLoot(Session killer, IReadOnlyList<(int Item, int Quantity)> drops)
    {
        var group = new List<Session> { killer };
        if (killer.Party is { } party)
        {
            var (zoneId, x, _, z, _) = killer.Position;
            group.AddRange(party.Members.Where(m => !ReferenceEquals(m, killer) && m.InZone && !m.Dead && m.Position.Zone == zoneId
                && Dist2D(m.Position.X, m.Position.Z, x, z) <= PartyShareRange));
        }
        return LootDeal.Deal(group, drops, Random.Shared).Select(d => (d.Owner, d.Drop.Item, d.Drop.Quantity)).ToList();
    }

    private void SharePartyXp(Session killer, int xp, Mob mob)
    {
        if (killer.Party is not { } party) return;
        var (zoneId, x, _, z, _) = killer.Position;
        foreach (var m in party.Members.Where(m => !ReferenceEquals(m, killer) && m.InZone && !m.Dead && m.Position.Zone == zoneId
                     && Dist2D(m.Position.X, m.Position.Z, x, z) <= PartyShareRange).ToList())
        {
            var before = m.Sheet!.Level;
            var level = AddXp(m, xp, out var sheet);
            if (level != before) AnnounceLevel(m, level);
            else SendLiveStats(m);
            Tell(m, $"+{xp} xp (party, {killer.Name}'s kill): {sheet.XpPercent:F0}% to next level.");
            QuestKill(m, mob); // party members' kill and loot stages count too
        }
    }
}
