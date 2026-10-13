using CH.Persistence;
using CH.Protocol;
using CH.Protocol.Messages;
using Lidgren.Network;
using Microsoft.Extensions.Logging;

namespace CH.Server;

/// <summary>
/// Mail (MailMessage 17012, MailNetworkHandler; docs/social-notes.md), opened at a mailbox NPC's MAIL action. Sending
/// costs Postage Stamps (1 + 1 per attached item) and can carry gold and up to two items; the server re-checks
/// everything and moves it in one transaction. Mail lasts 30 days (<b>our own</b> numbers; the client's limits are
/// in the MailList header). System mail (sender −1) is how the server sends rewards.
/// </summary>
public sealed partial class GameServer
{
    private const int MailMaxName = 30, MailMaxSubject = 50, MailMaxMessage = 500, MailMaxItems = 2, MailMinLevel = 1, MailDays = 30;
    private const int PostageStamp = 21948;

    // MailMessageType
    private const int MailList = 1, MailUnread = 2, MailUpdate = 3, MailSendInfo = 4;

    private void OnMail(Session s, NetIncomingMessage msg)
    {
        var c = s.Character!;
        switch (msg.ReadVariableInt32())
        {
            case 5:
                SendMailList(s);
                break;
            case 6:
                if (OwnMail(s, msg.ReadVariableInt32()) is { } open)
                {
                    _store.MarkMailRead(open.Id);
                    SendMailUpdate(s, open with { Read = true });
                }
                break;
            case 7:
                if (OwnMail(s, msg.ReadVariableInt32()) is { } deleted) _store.DeleteMail(deleted.Id);
                SendMailList(s);
                break;
            case 8:
                if (OwnMail(s, msg.ReadVariableInt32()) is { } returned) ReturnMail(s, returned);
                SendMailList(s);
                break;
            case 9:
                TakeAttachments(s, msg.ReadVariableInt32());
                break;
            case 10:
            {
                var name = msg.ReadString();
                var length = msg.ReadVariableInt32();
                var gold = msg.ReadVariableInt32();
                var items = msg.ReadVariableInt32();
                var recipient = _store.FindCharacter(StripName(name));
                var ok = recipient is not null && MailProblem(s, recipient, length, gold, items) is null;
                Send(s, Opcode.MailMessage, b =>
                {
                    b.WriteVariableInt32(MailSendInfo);
                    b.Write(name);
                    b.WriteVariableInt32(length);
                    b.WriteVariableInt32(gold);
                    b.WriteVariableInt32(items);
                    b.Write((byte)(ok ? 1 : 0));
                    if (!ok) return;
                    SocialEntry(recipient!.Id)!.Write(b);
                    b.WriteVariableInt32(1 + items);
                });
                if (!ok) Tell(s, recipient is null ? $"There's no one called {StripName(name)}." : MailProblem(s, recipient, length, gold, items)!);
                break;
            }
            case 11:
            {
                msg.ReadVariableInt32(); // postage as the client understood it; recomputed here
                var recipientId = msg.ReadVariableInt32();
                var subject = msg.ReadString();
                var body = msg.ReadString();
                var gold = msg.ReadVariableInt32();
                var count = msg.ReadVariableInt32();
                var attached = new List<(int InventoryId, int Template, int Quantity)>();
                for (var i = 0; i < count; i++)
                {
                    var inv = msg.ReadVariableInt32();
                    var template = msg.ReadVariableInt32();
                    var qty = msg.ReadVariableInt32();
                    if (inv >= 0 && template >= 0 && qty > 0) attached.Add((inv, template, qty));
                }
                SendMail(s, recipientId, subject, body, gold, attached);
                break;
            }
        }
    }

    /// <summary>The compose screen's pickers add " [..]" or "#.." to names.</summary>
    private static string StripName(string name)
    {
        var cut = name.IndexOfAny(['[', '#']);
        return (cut >= 0 ? name[..cut] : name).Trim();
    }

    private string? MailProblem(Session s, CharacterRecord recipient, int length, int gold, int items) =>
        recipient.Id == s.Character!.Id ? "You can't send mail to yourself."
        : _store.Blocks(recipient.Id, s.Character.Id) ? $"{recipient.Name} isn't taking your mail."
        : length > MailMaxMessage ? "That message is too long."
        : items > MailMaxItems ? $"You can attach at most {MailMaxItems} items."
        : gold < 0 || gold > _store.GetProgress(s.Character.Id).Coins ? "You don't have that much gold."
        : Sheet(s).Backpack.Where(i => i.TemplateId == PostageStamp).Sum(i => i.Quantity) < 1 + items ? $"You need {1 + items} Postage Stamps."
        : null;

    private void SendMail(Session s, int recipientId, string subject, string body, int gold, List<(int InventoryId, int Template, int Quantity)> attached)
    {
        var c = s.Character!;
        var recipient = _store.GetCharacter(recipientId);
        var backpack = Sheet(s).Backpack;
        var items = new List<MailItem>();
        string? problem = recipient is null ? "There's no one by that name." : MailProblem(s, recipient, body.Length, gold, attached.Count);
        problem ??= string.IsNullOrWhiteSpace(subject) || subject.Length > MailMaxSubject ? "The subject must be 1-50 characters." : null;
        foreach (var (inv, template, qty) in attached)
        {
            var item = backpack.FirstOrDefault(i => i.Id == inv && i.TemplateId == template);
            var info = _balance.Items.GetValueOrDefault(template);
            if (item is null || qty > item.Quantity) problem ??= "You don't have that item any more.";
            else if (item.Bound || info is null || !info.Tradeable || info.ImportantItem) problem ??= $"{info?.Name ?? "That item"} can't be sent.";
            else items.Add(new MailItem(0, template, qty, false, item.Charges));
        }
        if (attached.Select(a => a.InventoryId).Distinct().Count() != attached.Count) problem ??= "That item is attached twice.";
        if (problem is not null)
        {
            Tell(s, problem);
            return;
        }
        var mail = new MailRecord(0, recipient!.Id, c.Id, c.Name, subject.Trim(), body, gold, false, DateTimeOffset.UtcNow.AddDays(MailDays).ToUnixTimeSeconds());
        _store.SendMailFrom(c.Id, mail, attached.Select(a => ((long)a.InventoryId, a.Quantity)).ToList(), items, 1 + items.Count, PostageStamp);
        Send(s, Opcode.InventoryUpdate, RefreshSheet(s).InventoryWithMoney().Write);
        Tell(s, $"Mail sent to {recipient.Name}.");
        if (OnlineById(recipient.Id) is { } online) SendMailNotice(online);
        _log.LogInformation("{Name} mailed {Recipient}: {Gold} gold, {Items} items", s.Name, recipient.Name, gold, items.Count);
    }

    /// <summary>A system mail (sender −1, no reply): how the server sends rewards (tarot prizes, returns).</summary>
    private void SendSystemMail(long recipientId, string from, string subject, string body, int gold, IEnumerable<(int Template, int Quantity)> items)
    {
        _store.SendMail(new MailRecord(0, recipientId, -1, from, subject, body, gold, false, DateTimeOffset.UtcNow.AddDays(MailDays).ToUnixTimeSeconds()),
            items.Select(i => new MailItem(0, i.Template, i.Quantity, false, _balance.Items.GetValueOrDefault(i.Template)?.MaxCharges ?? 1)));
        if (OnlineById(recipientId) is { } online) SendMailNotice(online);
    }

    private void ReturnMail(Session s, MailRecord mail)
    {
        var items = _store.ListMailItems(mail.Id);
        if (mail.SenderId < 0 || mail.SenderId == s.Character!.Id || (mail.Gold == 0 && items.Count == 0)) return;
        _store.SendMail(new MailRecord(0, mail.SenderId, s.Character.Id, s.Name, $"Returned: {mail.Subject}", mail.Body, mail.Gold, false,
            DateTimeOffset.UtcNow.AddDays(MailDays).ToUnixTimeSeconds()), items);
        _store.DeleteMail(mail.Id);
        if (OnlineById(mail.SenderId) is { } sender) SendMailNotice(sender);
    }

    private void TakeAttachments(Session s, int mailId)
    {
        var c = s.Character!;
        var mails = _store.ListMail(c.Id).Where(m => (mailId == -1 || m.Id == mailId) && (m.Gold > 0 || m.ItemCount > 0)).ToList();
        foreach (var m in mails)
        {
            foreach (var item in _store.ListMailItems(m.Id))
            {
                if (_balance.Items.GetValueOrDefault(item.TemplateId) is { Stackable: true }) GrantItem(c.Id, item.TemplateId, item.Quantity);
                else _store.RestoreItem(c.Id, item.TemplateId, item.Quantity, item.Bound, item.Charges);
            }
            if (m.Gold > 0) _store.AddCurrency(c.Id, m.Gold, 0);
            _store.EmptyMail(m.Id);
        }
        Send(s, Opcode.InventoryUpdate, RefreshSheet(s).InventoryWithMoney().Write);
        SendMailList(s);
    }

    private MailRecord? OwnMail(Session s, int mailId) => _store.ListMail(s.Character!.Id).FirstOrDefault(m => m.Id == mailId);

    private void SendMailNotice(Session s) => Send(s, Opcode.MailMessage, b => b.WriteVariableInt32(MailUnread));

    private void SendMailList(Session s)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var expired in _store.ListMail(s.Character!.Id).Where(m => m.ExpiresAt <= now)) _store.DeleteMail(expired.Id);
        var mails = _store.ListMail(s.Character.Id);
        Send(s, Opcode.MailMessage, b =>
        {
            b.WriteVariableInt32(MailList);
            b.WriteVariableInt32(MailMaxName);
            b.WriteVariableInt32(MailMaxSubject);
            b.WriteVariableInt32(MailMaxMessage);
            b.WriteVariableInt32(MailMaxItems);
            b.WriteVariableInt32(MailMinLevel);
            b.WriteVariableInt32(mails.Count);
            foreach (var m in mails) WriteMailSummary(b, m, now);
        });
    }

    private static void WriteMailSummary(NetBuffer b, MailRecord m, long now)
    {
        b.WriteVariableInt32((int)m.Id);
        b.WriteVariableInt32((int)m.SenderId);
        b.Write(m.SenderName);
        b.Write(m.Subject);
        b.WriteVariableInt32(m.Gold);
        b.Write((byte)(m.ItemCount > 0 ? 1 : 0));
        b.Write((byte)(m.Read ? 1 : 0));
        b.WriteVariableInt32((int)Math.Max(0, (m.ExpiresAt - now) / 86400));
    }

    private void SendMailUpdate(Session s, MailRecord m)
    {
        var items = _store.ListMailItems(m.Id);
        Send(s, Opcode.MailMessage, b =>
        {
            b.WriteVariableInt32(MailUpdate);
            b.Write((byte)1);
            WriteMailSummary(b, m, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            b.Write(m.Body);
            b.WriteVariableInt32(items.Count);
            foreach (var i in items)
            {
                b.WriteVariableInt32((int)i.Id);
                b.WriteVariableInt32(i.TemplateId);
                b.WriteVariableInt32(i.Quantity);
                b.Write((byte)(i.Bound ? 1 : 0));
                b.WriteVariableInt32(i.Charges);
            }
        });
    }
}
