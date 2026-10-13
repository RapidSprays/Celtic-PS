using System.Text.Json;
using CH.Protocol;
using Lidgren.Network;
using Microsoft.Extensions.Logging;

namespace CH.Server;

/// <summary>
/// The living calendar (LivingCalendarInfo 21016 / LivingCalendarAction 21017; docs/content-systems-notes.md): one
/// daily login track from server/content/calendar.json: one reward per UTC day, in order, 28 to a cycle. The client's
/// Claim button throws before it sends anything (LivingCalendarManager.SendAction) and then leaves its screen stuck open,
/// so the calendar screen is never opened for players: the day's reward is given at the first login of the day, with a
/// chat line. The screen still works if the client asks for it. The track and rewards are <b>our own</b>.
/// </summary>
public sealed partial class GameServer
{
    private sealed record CalendarTrack(int Id, string Name, string Description, IReadOnlyList<(int Template, int Quantity)> Rewards);

    private readonly CalendarTrack? _calendar;

    private static CalendarTrack? LoadCalendar(string? path)
    {
        if (path is null || !File.Exists(path)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var e = doc.RootElement;
        return new CalendarTrack(e.GetProperty("trackId").GetInt32(), e.GetProperty("name").GetString() ?? "", e.GetProperty("description").GetString() ?? "",
            e.GetProperty("rewards").EnumerateArray().Select(r => (r[0].GetInt32(), r[1].GetInt32())).ToList());
    }

    private static int CalendarToday => (int)(DateTime.UtcNow - DateTime.UnixEpoch).TotalDays;
    private static DateTime NextCalendarDay => DateTime.UtcNow.Date.AddDays(1);

    /// <summary>Today's position on the track and whether it's been claimed.</summary>
    private (int DayIndex, bool Claimed) CalendarState(long characterId, CalendarTrack track)
    {
        var (claimed, lastDay) = _store.GetCalendar(characterId, track.Id);
        var today = lastDay == CalendarToday;
        return (today ? (claimed - 1) % track.Rewards.Count : claimed % track.Rewards.Count, today);
    }

    private void SendCalendarIfDue(Session s)
    {
        if (_calendar is null || s.CalendarOffered) return;
        s.CalendarOffered = true;
        if (CalendarState(s.Character!.Id, _calendar).Claimed) return;
        var (day, _) = CalendarState(s.Character.Id, _calendar);
        var (template, quantity) = _calendar.Rewards[day];
        if (ClaimCalendarReward(s, notify: false))
            Tell(s, $"Daily reward (day {day + 1} of {_calendar.Rewards.Count}): {(quantity > 1 ? $"{quantity} × " : "")}{_balance.Items.GetValueOrDefault(template)?.Name ?? "a gift"}. Come back tomorrow for the next one!");
    }

    private void SendCalendar(Session s)
    {
        if (_calendar is not { } track) return;
        var (day, claimed) = CalendarState(s.Character!.Id, track);
        Send(s, Opcode.LivingCalendarInfo, b =>
        {
            b.Write((byte)0); // FullCalendarData
            b.WriteVariableUInt32(1);
            b.WriteVariableUInt32((uint)track.Id);
            b.Write((byte)0); // free track
            b.Write(track.Name);
            b.Write(track.Description);
            b.WriteVariableInt32(day + 1);
            b.WriteVariableInt32(0);
            b.WriteVariableInt32(claimed ? -1 : day); // the client reads "claimed" as missed + this < day
            b.WriteVariableInt32(0); // missed days
            b.WriteVariableInt64(DateTime.UtcNow.ToBinary());
            b.WriteVariableInt64(NextCalendarDay.ToBinary());
            b.WriteVariableInt32(0);
            b.WriteVariableInt32(0);
            b.WriteVariableInt32(track.Rewards.Count);
            for (var i = 0; i < track.Rewards.Count; i++)
            {
                b.WriteVariableInt32(i);
                b.Write((byte)0); // item
                b.WriteVariableInt32(track.Rewards[i].Template);
                b.WriteVariableInt32(track.Rewards[i].Quantity);
                b.Write(false);
            }
        });
    }

    private void OnCalendarAction(Session s, NetIncomingMessage msg)
    {
        switch (msg.ReadByte())
        {
            case 0:
                SendCalendar(s);
                break;
            case 1:
                msg.ReadVariableUInt32();
                ClaimCalendarReward(s);
                break;
        }
    }

    private bool ClaimCalendarReward(Session s, bool notify = true)
    {
        if (_calendar is not { } track) return false;
        var c = s.Character!;
        var (day, claimed) = CalendarState(c.Id, track);
        if (claimed)
        {
            if (notify) Send(s, Opcode.LivingCalendarInfo, b => { b.Write((byte)2); b.Write("Today's reward is already claimed. Come back tomorrow!"); });
            return false;
        }
        var (total, _) = _store.GetCalendar(c.Id, track.Id);
        var (template, quantity) = track.Rewards[day];
        GrantItem(c.Id, template, quantity);
        _store.SaveCalendar(c.Id, track.Id, total + 1, CalendarToday);
        Send(s, Opcode.InventoryUpdate, RefreshSheet(s).InventoryWithMoney().Write);
        if (notify) Send(s, Opcode.LivingCalendarInfo, b =>
        {
            b.Write((byte)3); // RewardGranted
            b.WriteVariableUInt32((uint)track.Id);
            b.Write(day);
            b.Write(true);
            b.Write(0);
            b.Write(NextCalendarDay.Ticks);
            b.Write(0);
        });
        _log.LogInformation("{Name} claimed calendar day {Day}: {Quantity} × {Item}", s.Name, day + 1, quantity, _balance.Items.GetValueOrDefault(template)?.Name);
        return true;
    }
}
