using CH.Persistence;
using CH.Protocol;
using CH.Protocol.Messages;
using Lidgren.Network;
using Microsoft.Extensions.Logging;

namespace CH.Server;

/// <summary>
/// Tarot (TarotGameMessage 21030; docs/content-systems-notes.md), the Fortune Teller's pairs game: 18 face-down
/// cards, 9 pairs, a move is two flips, 18 moves. The decks follow her own words: Minor Arcana free once a day, Major
/// Arcana once a week, Premium Arcana five a day; we price the last two at one Tarot Token each (her Major price is a
/// seasonal token). A win mails the deck's Arcana Bag; a lost Premium game mails a Bad Luck token. Out of moves, 6 more
/// can be bought once for gold. Costs, attempts and rewards are <b>our own</b> where she doesn't say.
/// </summary>
public sealed partial class GameServer
{
    private const int TarotCards = 18, TarotPairs = 9, TarotMoves = 18, TarotExtraMoves = 6, TarotExtraMovesGold = 5_000, TarotToken = 73651;

    private sealed record TarotDeck(int Id, string Name, int Assets, bool Weekly, int Attempts, int CostItem, int CostQuantity, int Reward, int LossReward);

    private static readonly TarotDeck[] TarotDecks =
    [
        new(0, "Minor Arcana", 0, false, 1, -1, 0, 73648, -1),
        new(1, "Major Arcana", 1, true, 1, TarotToken, 1, 73649, -1),
        new(2, "Premium Arcana", 2, false, 5, TarotToken, 1, 73650, 74597),
    ];

    private sealed class TarotGame
    {
        public required TarotDeck Deck { get; init; }
        public required int[] Board { get; init; }
        public bool[] Matched { get; } = new bool[TarotCards];
        public int? FirstFlip { get; set; }
        public int MovesLeft { get; set; } = TarotMoves;
        public bool ExtraPurchased { get; set; }
        public bool OutOfMoves { get; set; }
    }

    private readonly Dictionary<long, TarotGame> _tarot = new();

    private static int TarotCycle(TarotDeck deck)
    {
        var day = (int)(DateTime.UtcNow - DateTime.UnixEpoch).TotalDays;
        return deck.Weekly ? (day + 3) / 7 : day; // weeks start on Monday (1970-01-01 was a Thursday)
    }

    private static DateTime TarotRefresh(TarotDeck deck)
    {
        var today = DateTime.UtcNow.Date;
        return deck.Weekly ? today.AddDays(7 - ((int)today.DayOfWeek + 6) % 7) : today.AddDays(1);
    }

    private int TarotAttemptsLeft(long characterId, TarotDeck deck)
    {
        var (cycle, plays, _) = _store.GetTarot(characterId, deck.Id);
        return Math.Max(0, deck.Attempts - (cycle == TarotCycle(deck) ? plays : 0));
    }

    private void OnTarot(Session s, NetIncomingMessage msg)
    {
        switch (msg.ReadVariableInt32())
        {
            case 1:
                SendTarotDecks(s);
                break;
            case 2:
                StartTarot(s, msg.ReadVariableInt32());
                break;
            case 3:
            {
                var deck = msg.ReadVariableInt32();
                FlipTarot(s, deck, msg.ReadVariableInt32());
                break;
            }
            case 4:
            {
                msg.ReadVariableInt32();
                TarotExtra(s, msg.ReadBoolean());
                break;
            }
        }
    }

    private void SendTarotDecks(Session s)
    {
        var c = s.Character!;
        var current = _tarot.GetValueOrDefault(c.Id);
        Send(s, Opcode.TarotGameMessage, b =>
        {
            b.WriteVariableInt32(1);
            b.WriteVariableInt32(TarotDecks.Length);
            foreach (var d in TarotDecks)
            {
                b.WriteVariableInt32(d.Id);
                b.Write(d.Name);
                b.WriteVariableInt32(d.Assets);
                b.WriteVariableInt64(TarotRefresh(d).Ticks); // must lie ahead, or the client asks again every frame
                b.WriteVariableInt32(TarotAttemptsLeft(c.Id, d));
                b.WriteVariableInt32(d.CostItem);
                b.WriteVariableInt32(d.CostQuantity);
                b.Write(current?.Deck.Id == d.Id);
                b.Write(d.LossReward > 0);
                b.WriteVariableInt32(TarotExtraMoves);
                b.WriteVariableInt32(TarotExtraMovesGold);
            }
        });
    }

    private void StartTarot(Session s, int deckId)
    {
        var c = s.Character!;
        var deck = TarotDecks.FirstOrDefault(d => d.Id == deckId);
        void Refuse(string why) => Send(s, Opcode.TarotGameMessage, b => { b.WriteVariableInt32(2); b.WriteVariableInt32(deckId); b.Write(false); b.Write(why); });
        if (deck is null)
        {
            Refuse("There's no such deck.");
            return;
        }
        if (_tarot.TryGetValue(c.Id, out var running) && running.Deck.Id == deckId)
        {
            Send(s, Opcode.TarotGameMessage, b =>
            {
                b.WriteVariableInt32(2);
                b.WriteVariableInt32(deckId);
                b.Write(true);
                b.Write(false); // continuing
                b.Write(running.ExtraPurchased);
                for (var i = 0; i < TarotCards; i++) b.WriteVariableInt32(running.Matched[i] ? running.Board[i] : -1);
                b.WriteVariableInt32(running.MovesLeft);
            });
            return;
        }
        var held = Sheet(s).Backpack.Where(i => i.TemplateId == deck.CostItem).Sum(i => i.Quantity);
        if (TarotAttemptsLeft(c.Id, deck) == 0)
        {
            Refuse(deck.Weekly ? "You've played this deck this week. Come back next week." : "You've played this deck enough today. Come back tomorrow.");
            return;
        }
        if (deck.CostItem > 0 && held < deck.CostQuantity)
        {
            Refuse($"This deck costs {deck.CostQuantity} {_balance.Items.GetValueOrDefault(deck.CostItem)?.Name ?? "token"}.");
            return;
        }
        if (running is not null) EndTarot(s, running, won: false); // a new deck ends the one in progress
        if (deck.CostItem > 0) TakeItems(c.Id, deck.CostItem, deck.CostQuantity);
        var (cycle, plays, wins) = _store.GetTarot(c.Id, deck.Id);
        _store.SaveTarot(c.Id, deck.Id, TarotCycle(deck), cycle == TarotCycle(deck) ? plays + 1 : 1, wins);
        var board = Enumerable.Range(0, TarotCards).Select(i => i % TarotPairs).OrderBy(_ => Random.Shared.Next()).ToArray();
        var game = new TarotGame { Deck = deck, Board = board };
        _tarot[c.Id] = game;
        var sheet = RefreshSheet(s);
        Send(s, Opcode.TarotGameMessage, b =>
        {
            b.WriteVariableInt32(2);
            b.WriteVariableInt32(deckId);
            b.Write(true);
            b.Write(true); // new game
            b.WriteVariableInt32(game.MovesLeft);
            var items = sheet.BackpackSlots();
            b.WriteVariableInt32(items.Count);
            foreach (var i in items) i.Write(b);
        });
    }

    private void FlipTarot(Session s, int deckId, int index)
    {
        if (!_tarot.TryGetValue(s.Character!.Id, out var game) || game.Deck.Id != deckId || game.OutOfMoves
            || index is < 0 or >= TarotCards || game.Matched[index] || game.FirstFlip == index) return;
        if (game.FirstFlip is not { } first)
        {
            game.FirstFlip = index;
            Send(s, Opcode.TarotGameMessage, b =>
            {
                b.WriteVariableInt32(3);
                b.WriteVariableInt32(deckId);
                b.WriteVariableInt32(index);
                b.WriteVariableInt32(game.Board[index]);
                b.Write(false);
            });
            return;
        }
        game.FirstFlip = null;
        game.MovesLeft--;
        var match = game.Board[first] == game.Board[index];
        if (match) game.Matched[first] = game.Matched[index] = true;
        var won = game.Matched.All(m => m);
        var finished = won || game.MovesLeft <= 0;
        Send(s, Opcode.TarotGameMessage, b =>
        {
            b.WriteVariableInt32(3);
            b.WriteVariableInt32(deckId);
            b.WriteVariableInt32(first);
            b.WriteVariableInt32(game.Board[first]);
            b.Write(true);
            b.WriteVariableInt32(index);
            b.WriteVariableInt32(game.Board[index]);
            b.Write(match);
            b.Write(finished);
            b.Write(won);
            b.WriteVariableInt32(game.MovesLeft);
        });
        if (won) EndTarot(s, game, won: true);
        else if (finished && !game.ExtraPurchased) game.OutOfMoves = true; // the client offers extra moves; the loss waits for the answer
        else if (finished) EndTarot(s, game, won: false);
    }

    private void TarotExtra(Session s, bool accept)
    {
        var c = s.Character!;
        if (!_tarot.TryGetValue(c.Id, out var game) || !game.OutOfMoves) return;
        if (!accept)
        {
            EndTarot(s, game, won: false);
            return;
        }
        if (_store.GetProgress(c.Id).Coins < TarotExtraMovesGold)
        {
            Send(s, Opcode.TarotGameMessage, b => { b.WriteVariableInt32(4); b.Write(false); b.Write($"Extra moves cost {TarotExtraMovesGold:N0} gold."); });
            EndTarot(s, game, won: false);
            return;
        }
        _store.AddCurrency(c.Id, -TarotExtraMovesGold, 0);
        game.OutOfMoves = false;
        game.ExtraPurchased = true;
        game.MovesLeft += TarotExtraMoves;
        var sheet = RefreshSheet(s);
        Send(s, Opcode.TarotGameMessage, b =>
        {
            b.WriteVariableInt32(4);
            b.Write(true);
            b.WriteVariableInt32(game.MovesLeft);
            sheet.InventoryWithMoney().Write(b);
        });
    }

    /// <summary>The game is over: the prize (or the bad-luck token) comes by mail, as the client tells the player.</summary>
    private void EndTarot(Session s, TarotGame game, bool won)
    {
        var c = s.Character!;
        _tarot.Remove(c.Id);
        if (won)
        {
            var (cycle, plays, wins) = _store.GetTarot(c.Id, game.Deck.Id);
            _store.SaveTarot(c.Id, game.Deck.Id, cycle, plays, wins + 1);
            SendSystemMail(c.Id, "Fortune Teller", $"{game.Deck.Name}: you won!", "The cards favoured you. Here is your prize.", 0, [(game.Deck.Reward, 1)]);
        }
        else if (game.Deck.LossReward > 0)
            SendSystemMail(c.Id, "Fortune Teller", $"{game.Deck.Name}: better luck next time", "The cards were unkind. Take this for your trouble.", 0, [(game.Deck.LossReward, 1)]);
        _log.LogInformation("{Name} {Result} at {Deck}", s.Name, won ? "won" : "lost", game.Deck.Name);
    }
}
