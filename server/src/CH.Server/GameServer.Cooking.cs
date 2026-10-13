using System.Text.Json;
using CH.Persistence;
using CH.Protocol;
using CH.Protocol.Messages;
using CH.Server.Game;
using Lidgren.Network;
using Microsoft.Extensions.Logging;

namespace CH.Server;

/// <summary>
/// Cooking (CraftingMessage 23001; docs/gathering-notes.md). An oven or the chef's COOKING 221 action opens the
/// crafting page, which asks for the known recipes, then token vendor 221's "stock" (one entry per recipe: the dish,
/// and its cost id, whose cost list is the ingredients), then sends one craft request per dish. The outcome uses the
/// client's own chance curve on Cooking Proficiency (38) and Mastery (39) and arrives after the recipe's cooking time;
/// the client then "buys" the dish from vendor 221, which is answered without charge (the dish is already given).
/// Ingredient lists are <b>our own</b> (scripts/gen-cooking.py); everything else is the client's recipe data.
/// </summary>
public sealed partial class GameServer
{
    private const int CookingVendor = 221, CookingStartRecipeLevel = 20;

    private sealed record PendingCraft(Session Session, RecipeInfo Recipe, int Result, int Xp, double ReadyAt);

    private readonly IReadOnlyDictionary<int, IReadOnlyList<(int Template, int Quantity)>> _ingredients;
    private readonly List<PendingCraft> _crafts = new();

    private static IReadOnlyDictionary<int, IReadOnlyList<(int, int)>> LoadIngredients(string? path)
    {
        var map = new Dictionary<int, IReadOnlyList<(int, int)>>();
        if (path is null || !File.Exists(path)) return map;
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        foreach (var r in doc.RootElement.GetProperty("recipes").EnumerateObject())
            map[int.Parse(r.Name)] = r.Value.GetProperty("ingredients").EnumerateArray().Select(i => (i[0].GetInt32(), i[1].GetInt32())).ToList();
        return map;
    }

    /// <summary>Recipes with ingredients: those learnt from recipe books, plus the beginners' ones (level 20 and below).</summary>
    private List<RecipeInfo> KnownRecipes(Session s)
    {
        var learnt = _store.ListRecipes(s.Character!.Id).ToHashSet();
        return _balance.RecipeTemplates.Values.Where(r => _ingredients.ContainsKey(r.CostId) && (learnt.Contains(r.Id) || r.Level <= CookingStartRecipeLevel))
            .OrderBy(r => r.Level).ThenBy(r => r.Id).ToList();
    }

    private void OnCrafting(Session s, NetIncomingMessage msg)
    {
        switch (msg.ReadVariableInt32())
        {
            case 1:
                SendKnownRecipes(s);
                break;
            case 3:
            {
                var recipeId = msg.ReadVariableInt32();
                Cook(s, recipeId, msg.ReadVariableInt32());
                break;
            }
            case 5:
                if (msg.ReadBoolean()) _crafts.RemoveAll(c => ReferenceEquals(c.Session, s)); // left the page: unfinished dishes are lost
                break;
            case 6:
            {
                var recipeId = msg.ReadVariableInt32();
                _store.SetRecipeFavourite(s.Character!.Id, recipeId, msg.ReadBoolean());
                break;
            }
        }
    }

    private void SendKnownRecipes(Session s)
    {
        var known = KnownRecipes(s);
        var favourites = _store.ListRecipeFavourites(s.Character!.Id);
        var sheet = s.Sheet!;
        Send(s, Opcode.CraftingMessage, b =>
        {
            b.WriteVariableUInt32(2);
            b.WriteVariableInt32(known.Count);
            foreach (var r in known)
            {
                b.WriteVariableInt32(r.Id);
                b.Write(favourites.Contains(r.Id));
            }
            b.WriteVariableInt32(sheet.CookingLevel);
            b.Write(sheet.CookingXpPercent);
        });
    }

    /// <summary>Vendor 221's "stock": each known recipe's dish under its cost id, and the ingredients as that cost.</summary>
    private void SendCookingVendor(Session s)
    {
        var known = KnownRecipes(s);
        Send(s, Opcode.TokenVendorMessage, b =>
        {
            b.WriteVariableInt32(2);
            b.Write((byte)1);
            b.WriteVariableInt32(CookingVendor);
            b.WriteVariableInt32(known.Count);
            if (known.Count == 0)
            {
                b.Write("You don't know any recipes yet.");
                b.WriteVariableInt32(0);
                return;
            }
            foreach (var r in known)
            {
                b.WriteVariableInt32(r.Id);
                b.WriteVariableInt32(r.Result);
                b.WriteVariableInt32(1);
                b.WriteVariableInt32(r.CostId);
            }
            b.WriteVariableInt32(known.Count);
            foreach (var r in known)
            {
                var cost = _ingredients[r.CostId];
                b.WriteVariableInt32(r.CostId);
                b.WriteVariableInt32(cost.Count);
                foreach (var (template, qty) in cost)
                {
                    b.WriteVariableInt32(template);
                    b.WriteVariableInt32(qty);
                }
            }
        });
    }

    private void Cook(Session s, int recipeId, int optionalTemplate)
    {
        var c = s.Character!;
        var recipe = KnownRecipes(s).FirstOrDefault(r => r.Id == recipeId);
        var backpack = Sheet(s).Backpack;
        int Held(int template) => backpack.Where(i => i.TemplateId == template).Sum(i => i.Quantity);
        var useOptional = optionalTemplate > 0 && recipe is not null && optionalTemplate == recipe.OptionalId && Held(optionalTemplate) > 0;
        var problem = recipe is null ? "You don't know that recipe."
            : _ingredients[recipe.CostId].Any(i => Held(i.Template) < i.Quantity) ? $"You don't have the ingredients for {recipe.Name}."
            : _crafts.Any(p => ReferenceEquals(p.Session, s)) ? "You're already cooking."
            : null;
        if (problem is not null)
        {
            Tell(s, problem);
            return;
        }
        _store.Atomically(() =>
        {
            foreach (var (template, qty) in _ingredients[recipe!.CostId]) TakeItems(c.Id, template, qty);
            if (useOptional) TakeItems(c.Id, optionalTemplate, 1);
        });
        var sheet = RefreshSheet(s);
        var proficiency = sheet.AbilityLevel(CookingProficiency);
        var mastery = sheet.AbilityLevel(CookingMastery);
        var success = useOptional ? 1.0 : RecipeInfo.Chance(proficiency, recipe!.MinAbility, recipe.Success, recipe.Difficulty);
        var critical = RecipeInfo.Chance(mastery, recipe.MinAbility, recipe.Critical, recipe.Difficulty) * (useOptional ? 2 : 1);
        var master = RecipeInfo.Chance(mastery, recipe.MinAbility, recipe.Master, recipe.Difficulty);
        var result = Random.Shared.NextDouble() >= success ? recipe.Failure
            : Random.Shared.NextDouble() < master ? recipe.MasterResult
            : Random.Shared.NextDouble() < critical ? recipe.CriticalResult
            : recipe.Result;
        TrainAbility(s, CookingProficiency);
        if (result != recipe.Result && result != recipe.Failure) TrainAbility(s, CookingMastery);
        // Dishes far below one's level teach little.
        var gap = sheet.CookingLevel - recipe.Level;
        var xp = result == recipe.Failure ? recipe.Xp / 4 : gap > 20 ? recipe.Xp / 4 : recipe.Xp;
        var professional = sheet.CookingLevel > recipe.Level + 20;
        _crafts.Add(new PendingCraft(s, recipe, result, Math.Max(1, xp), NetTime.Now + (recipe.CraftingTime + (professional ? 0 : 7)) * _options.CookingTimeScale));
    }

    /// <summary>Finished dishes: the result, cooking XP and level (CraftingMessage sub 4).</summary>
    private void TickCrafts(double now)
    {
        foreach (var craft in _crafts.Where(c => now >= c.ReadyAt).ToList())
        {
            _crafts.Remove(craft);
            var s = craft.Session;
            if (s.Character is null) continue;
            GrantItem(s.Character.Id, craft.Result, 1);
            var (_, _, level, xp) = _store.GetCrafts(s.Character.Id);
            var (newLevel, newXp) = Crafting.Add(level, xp, craft.Xp);
            _store.SaveCooking(s.Character.Id, newLevel, newXp);
            var sheet = RefreshSheet(s);
            Send(s, Opcode.CraftingMessage, b =>
            {
                b.WriteVariableUInt32(4);
                b.WriteVariableInt32(craft.Result);
                b.WriteVariableInt32(sheet.CookingLevel);
                b.Write(sheet.CookingXpPercent);
                b.WriteVariableInt32(craft.Xp);
            });
            if (newLevel > level)
            {
                var (available, notYet) = QuestLists(s);
                Send(s, Opcode.LevelUp, new LevelUp(newLevel) { LevelType = 2, AvailableQuests = available, NotYetAvailableQuests = notYet }.Write);
            }
            QuestGathered(s, craft.Result, 1);
        }
    }

    /// <summary>The crafting page "buys" each finished dish from vendor 221: it's already in the backpack.</summary>
    private void ConfirmCookedDish(Session s, int stockId)
    {
        var sheet = RefreshSheet(s);
        var dish = _balance.RecipeTemplates.GetValueOrDefault(stockId)?.Result;
        var item = sheet.Backpack.Where(i => dish is null || i.TemplateId == dish).MaxBy(i => i.Id);
        Send(s, Opcode.TokenVendorMessage, b =>
        {
            b.WriteVariableInt32(4);
            b.Write((byte)1);
            b.WriteVariableInt32((int)(item?.Id ?? 0));
            sheet.InventoryWithMoney().Write(b);
        });
    }
}
