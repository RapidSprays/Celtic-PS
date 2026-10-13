using CH.Persistence;
using CH.Protocol;
using CH.Protocol.Messages;
using Lidgren.Network;
using Microsoft.Extensions.Logging;

namespace CH.Server;

/// <summary>
/// Friends and the block list (docs/social-notes.md). Friendship is mutual and needs the other player's yes
/// (ClientFriendRequest 17004 → ServerFriendRequest 17006 → ClientFriendReply 17005 → ServerFriendReply 17007). The
/// client's popup times out silently, so the server answers TIMEOUT itself. The client edits neither list locally:
/// every change is answered with the whole list, and online/offline changes are pushed to friends and clanmates. The
/// client never filters by its block list, so the server does: a blocked player's whispers, chat, invitations, trades,
/// duels and mail never reach the blocker.
/// </summary>
public sealed partial class GameServer
{
    private const double FriendRequestSeconds = 30;
    private const int FriendError = 0, FriendAccept = 1, FriendReject = 2, FriendTimeout = 3; // HW_FRIEND_REPLY

    /// <summary>Open friend requests: (target, requester) → when they lapse.</summary>
    private readonly Dictionary<(long Target, long Requester), double> _friendRequests = new();

    private Session? OnlineById(long characterId) => Online.FirstOrDefault(p => p.Character!.Id == characterId);

    /// <summary>A character as social lists show them, online or not.</summary>
    private SocialCharacter? SocialEntry(long characterId)
    {
        if (OnlineById(characterId) is { } p)
            return new SocialCharacter(p.Name, p.CharacterId, true, p.Position.Zone, p.Sheet!.Level, p.Character!.Class, p.Character.Race);
        if (_store.GetCharacter(characterId) is not { } c) return null;
        return new SocialCharacter(c.Name, (int)c.Id, false, c.Zone, _store.GetProgress(c.Id).Level, c.Class, c.Race);
    }

    private List<FriendEntry> FriendEntries(Session s) => _store.ListFriends(s.Character!.Id).Select(SocialEntry).OfType<SocialCharacter>()
        .Select(f => new FriendEntry(f.Name, f.Id, f.Online, f.Zone, f.Level, f.Class)).ToList();

    private void SendFriendList(Session s) => Send(s, Opcode.FriendList, b => FriendListMessage.Write(b, FriendEntries(s)));

    private void SendBlockedList(Session s) => Send(s, Opcode.BlockedList, b => BlockedListMessage.Write(b,
        s.Blocked.Select(id => _store.GetCharacter(id)).OfType<CharacterRecord>().Select(c => ((int)c.Id, c.Name)).ToList()));

    /// <summary>Someone logged in, out or changed zone: their friends' and clanmates' lists follow.</summary>
    private void SharePresence(Session s)
    {
        if (s.Character is null) return;
        foreach (var id in _store.ListFriends(s.Character.Id))
            if (OnlineById(id) is { } friend) SendFriendList(friend);
        if (_store.GetMembership(s.Character.Id) is { } m) SendClanToMembers(m.ClanId, except: s.Character.Id);
    }

    /// <summary>True when <paramref name="recipient"/> has blocked <paramref name="sender"/>.</summary>
    private static bool IsBlockedBy(Session sender, Session recipient) => sender.Character is { } c && recipient.Blocked.Contains(c.Id);

    private void OnSocialMessage(Session s, Opcode opcode, NetIncomingMessage msg)
    {
        switch (opcode)
        {
            case Opcode.ClientFriendRequest:
                RequestFriend(s, msg.ReadVariableInt32());
                break;
            case Opcode.ClientFriendReply:
            {
                var requester = msg.ReadVariableInt32();
                AnswerFriend(s, requester, msg.ReadVariableInt32());
                break;
            }
            case Opcode.RemoveFriend:
            {
                var other = msg.ReadVariableInt32();
                _store.RemoveFriends(s.Character!.Id, other);
                SendFriendList(s);
                if (OnlineById(other) is { } p) SendFriendList(p);
                break;
            }
            case Opcode.RequestSocialLists:
                SendFriendList(s);
                SendBlockedList(s);
                SendClanList(s);
                break;
            case Opcode.BlockCharacter:
                BlockCharacter(s, msg.ReadVariableInt32());
                break;
            case Opcode.UnblockCharacter:
            {
                var characterId = msg.ReadVariableInt32();
                var serverId = msg.ReadVariableInt32();
                var id = characterId >= 0 ? characterId : serverId;
                _store.Unblock(s.Character!.Id, id);
                s.Blocked.Remove(id);
                SendBlockedList(s);
                break;
            }
        }
    }

    private void RequestFriend(Session s, int targetId)
    {
        var target = OnlineByCharacter(targetId);
        void Answer(string name, int reply) => Send(s, Opcode.ServerFriendReply, b => { b.Write(name); b.WriteVariableInt32(reply); });
        if (target is null || ReferenceEquals(target, s))
        {
            Answer(target?.Name ?? "", FriendError);
            return;
        }
        if (_store.ListFriends(s.Character!.Id).Contains(target.Character!.Id))
        {
            Tell(s, $"{target.Name} is already your friend.");
            Answer(target.Name, FriendError);
            return;
        }
        if (IsBlockedBy(s, target))
        {
            Answer(target.Name, FriendReject);
            return;
        }
        _friendRequests[(target.Character.Id, s.Character.Id)] = NetTime.Now + FriendRequestSeconds;
        Send(target, Opcode.ServerFriendRequest, b =>
        {
            b.WriteVariableInt32(s.CharacterId);
            b.Write(s.Name);
        });
    }

    private void AnswerFriend(Session s, int requesterId, int reply)
    {
        if (!_friendRequests.Remove((s.Character!.Id, requesterId))) return;
        if (OnlineByCharacter(requesterId) is not { } requester) return;
        Send(requester, Opcode.ServerFriendReply, b =>
        {
            b.Write(s.Name);
            b.WriteVariableInt32(reply is FriendAccept or FriendReject ? reply : FriendReject);
        });
        if (reply != FriendAccept) return;
        _store.AddFriends(s.Character.Id, requesterId);
        SendFriendList(s);
        SendFriendList(requester);
        _log.LogInformation("{A} and {B} are now friends", requester.Name, s.Name);
    }

    /// <summary>Unanswered friend requests lapse (the client's popup closes without a word).</summary>
    private void ExpireFriendRequests(double now)
    {
        foreach (var (key, until) in _friendRequests.Where(r => r.Value <= now).ToList())
        {
            _friendRequests.Remove(key);
            if (OnlineById(key.Requester) is not { } requester) continue;
            var name = OnlineById(key.Target)?.Name ?? _store.GetCharacter(key.Target)?.Name ?? "";
            Send(requester, Opcode.ServerFriendReply, b => { b.Write(name); b.WriteVariableInt32(FriendTimeout); });
        }
    }

    private void BlockCharacter(Session s, int otherId)
    {
        var c = s.Character!;
        if (otherId == c.Id || _store.GetCharacter(otherId) is null) return;
        _store.Block(c.Id, otherId);
        s.Blocked.Add(otherId);
        if (_store.ListFriends(c.Id).Contains(otherId))
        {
            _store.RemoveFriends(c.Id, otherId);
            SendFriendList(s);
            if (OnlineById(otherId) is { } other) SendFriendList(other);
        }
        SendBlockedList(s);
    }
}
