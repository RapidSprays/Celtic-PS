using CH.Persistence;
using CH.Protocol;
using CH.Protocol.Messages;
using CH.Server.Game;
using Lidgren.Network;
using Microsoft.Extensions.Logging;

namespace CH.Server;

/// <summary>
/// Character management and quality of life (docs/porting-status.md §2): deleting characters, the barbershop
/// (BarbershopNetworkHandler), Backpack and Bank Expansions, and the client's settings report.
/// </summary>
public sealed partial class GameServer
{
    /// <summary>Bank slots each Bank Expansion adds ("Upgrade your bank account slot limit by an extra 10 slots").</summary>
    public const int BankSlotsPerExpansion = 10;

    /// <summary>Items each Backpack Expansion lets you carry before becoming over-encumbered.</summary>
    public const int BackpackItemsPerExpansion = 5;

    public const int BackpackExpansion = 13, BankExpansion = 92;

    /// <summary>DeleteCharacter (12007): string name, varuint id → DeleteCharacterReply (12008): byte status [string reason].</summary>
    private void OnDeleteCharacter(Session s, string name, uint characterId)
    {
        var character = _store.GetCharacter(characterId);
        string? problem = character is null || character.AccountId != s.Account!.Id ? "That character isn't yours."
            : !string.Equals(character.Name, name, StringComparison.OrdinalIgnoreCase) ? "The name doesn't match."
            : _sessions.Values.Any(o => o.Character?.Id == characterId && o.InZone) ? "That character is in the game right now."
            : null;
        if (problem is null && !_store.DeleteCharacter(s.Account!.Id, characterId)) problem = "That character couldn't be deleted.";
        if (problem is null) _log.LogInformation("Account {Account} deleted character {Name}", s.Account!.Username, name);
        Send(s, Opcode.DeleteCharacterReply, b =>
        {
            b.Write((byte)(problem is null ? CommandStatus.Accepted : CommandStatus.Rejected));
            if (problem is not null) b.Write(problem);
        });
    }

    /// <summary>
    /// BarbershopMessage (16009). Sub-op 1, purchase: varint cost, then the look; sub-op 3, the look again once the
    /// client accepted. The look: varint face, skin colour, hair, hair colour, face accessory, its colour, float height.
    /// Free on this server. Reply, sub-op 2: byte success, string text, byte "not enough platinum" popup, then on success
    /// the inventory with money.
    /// </summary>
    private void OnBarbershop(Session s, NetIncomingMessage msg)
    {
        var op = msg.ReadVariableInt32();
        if (op is not (1 or 3)) return;
        if (op == 1) msg.ReadVariableInt32(); // the client's price: ignored (free here)
        var (face, skinColour, hair, hairColour, accessory, accessoryColour) =
            (msg.ReadVariableInt32(), msg.ReadVariableInt32(), msg.ReadVariableInt32(), msg.ReadVariableInt32(), msg.ReadVariableInt32(), msg.ReadVariableInt32());
        var scale = msg.ReadFloat();
        if (!float.IsFinite(scale) || scale < 0.5f || scale > 1.5f) scale = s.Character!.Scale;
        _store.UpdateAppearance(s.Character!.Id, face, skinColour, hair, hairColour, accessory, accessoryColour, scale);
        s.Character = _store.GetCharacter(s.Character.Id);
        var sheet = RefreshSheet(s);
        if (op == 1)
        {
            Send(s, Opcode.BarbershopMessage, b =>
            {
                b.WriteVariableInt32(2);
                b.Write((byte)1);
                b.Write("");
                b.Write((byte)0);
                sheet.InventoryWithMoney().Write(b);
            });
        }
        // Others rebuild the model with the new look.
        if (s.InZone && _zones.TryGetValue(s.Position.Zone, out var zone))
        {
            AnnounceDeparture(zone, s);
            AnnounceArrival(zone, s, wasPresent: false);
        }
    }

    /// <summary>Backpack and Bank Expansions are applied when received (like sigils) and counted on the character.</summary>
    private bool TryGrantExpansion(long characterId, int templateId, int quantity)
    {
        if (templateId is not (BackpackExpansion or BankExpansion) || quantity <= 0) return false;
        var (backpack, bank) = templateId == BackpackExpansion ? (quantity, 0) : (0, quantity);
        _store.AddExpansions(characterId, backpack, bank);
        if (Online.FirstOrDefault(p => p.Character?.Id == characterId) is { } s)
        {
            var sheet = RefreshSheet(s);
            SendLiveStats(s); // the bank limit is in the stats block
            Tell(s, bank > 0
                ? $"{quantity} Bank Expansion{(quantity == 1 ? "" : "s")} applied: your bank holds {sheet.BankLimit} items."
                : $"{quantity} Backpack Expansion{(quantity == 1 ? "" : "s")} applied: you can carry {sheet.BackpackExpansions * BackpackItemsPerExpansion} more items before becoming over-encumbered.");
        }
        return true;
    }
}
