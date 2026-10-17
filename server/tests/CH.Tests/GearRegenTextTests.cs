using CH.Server.Game;
using Xunit;

namespace CH.Tests;

public class GearRegenTextTests
{
    [Theory]
    [InlineData("This quiver has been enchanted with the eldritch might of the ancients, ensuring the arrows held within and focussed with its power. Boosts attack speed by 30% and increases energy regeneration by 30 points per tick.", 0, 30)]
    [InlineData("This shinestone bracelet has been intricately crafted from pure gold and enchanted with arcane energies, the stone embedded within radiating a powerful blue light. The bracelet will boost your health regen by 30 and energy regen by 20.", 30, 20)]
    [InlineData("Restores 70 health and 50 energy per tick. Grants 'Cloak of the Eagle' which deals up to 400 slashing damage to enemies that attack you. When equipped with a Trailblazer's Band and Ring you gain the Trailblazer Aura increasing all attributes by 100.", 70, 50)]
    [InlineData("This amulet gives 60 health and 40 energy regen and grants the skill 'Stargem Armour' which increases your armour by 300 for 15 seconds.", 60, 40)]
    [InlineData("Restores 25 health and 15 energy per tick. You may only equip one Trailblazer's Ring. When equipped with a Trailblazer's Band and Amulet you gain the Trailblazer Aura increasing all attributes by 100 points.", 25, 15)]
    public void Live_regen_gear_is_read_from_its_description(string description, int health, int energy)
    {
        var regen = GearRegen.Parse(description);
        Assert.Equal((health, energy), (regen.Health, regen.Energy));
    }
}
