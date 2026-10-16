using System.Net;

namespace CH.Server;

public sealed class ServerOptions
{
    /// <summary>Address both listeners bind to. Loopback unless <see cref="AllowRemote"/> is set.</summary>
    public IPAddress BindAddress { get; set; } = IPAddress.Loopback;

    /// <summary>Required to bind a non-loopback address (docs/decisions.md: wider exposure is a separate decision).</summary>
    public bool AllowRemote { get; set; }

    /// <summary>HTTP "patch server" port. 0 picks a free port (tests).</summary>
    public int HttpPort { get; set; } = 5555;

    /// <summary>
    /// A second HTTP port, normally the launcher relay's 5565 that the game copies are patched to (<c>--client-port</c>).
    /// Then game windows started directly from the copy reach the server without the launcher, so several can run at
    /// once. The launcher can't run alongside it, since its relay needs the same port.
    /// </summary>
    public int? ClientHttpPort { get; set; }

    /// <summary>Lidgren UDP game server port. 0 picks a free port (tests).</summary>
    public int GamePort { get; set; } = 5556;

    /// <summary>
    /// Tailscale mode (<c>--tailscale</c>): listen on every interface but accept only this machine and Tailscale
    /// addresses, and advertise this machine's Tailscale IP.
    /// </summary>
    public bool TailscaleOnly { get; set; }

    /// <summary>Whether a connection from <paramref name="address"/> is accepted at all.</summary>
    public bool IsAllowedClient(IPAddress? address) =>
        address is not null && (!TailscaleOnly || NetworkAccess.IsLocalMachine(address) || NetworkAccess.IsTailscale(address));

    /// <summary>Address written into the world list for the client to connect to.</summary>
    public string PublicHost { get; set; } = "127.0.0.1";

    public string DataDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "data");

    public string DatabasePath => Path.Combine(DataDirectory, "private-server.db");

    public string LogDirectory => Path.Combine(DataDirectory, "logs");

    public int WorldId { get; set; } = 2; // the client auto-selects the first world with id > 1

    public string WorldName { get; set; } = "Private";

    public uint MaxCharacterSlots { get; set; } = 6;

    /// <summary>Reported patch level; anything ≤ the client's (143) means "no patching".</summary>
    public int PatchLevel { get; set; } = 0;

    /// <summary>Client network protocol version (EnvironmentSetup networkProtocolVersion = 1).</summary>
    public int ProtocolVersion { get; set; } = 1;

    /// <summary>
    /// Where new characters start: zone 2 "Lir's Reach", spawn point 2003 "Highshore Village", taken from the client
    /// balance data (Zones["2"].PlayerSpawnPoints). The original server's choice is unknown, so this is approximated.
    /// </summary>
    public (int Zone, float X, float Y, float Z, float Direction) DefaultSpawn { get; set; } = (2, -109.51f, -3.7f, -16.86f, 0f);

    /// <summary>The client's balance JSON extracted by scripts/extract-balance.py. Missing → the server runs without it.</summary>
    public string? BalancePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "CelticHeroesAnalysis", "extracted", "24928510", "balance-client.json");

    /// <summary>Generated monster camps (scripts/gen-zone-population.py), read from beside the balance JSON.</summary>
    public string? PopulationPath => BalancePath is null ? null : Path.Combine(Path.GetDirectoryName(BalancePath)!, "zone-population.json");

    /// <summary>Quests and NPC placements (scripts/gen-world.py), read from beside the balance JSON.</summary>
    public string? WorldPath => BalancePath is null ? null : Path.Combine(Path.GetDirectoryName(BalancePath)!, "world.json");

    /// <summary>Multiplies every recipe's cooking time (tests use 0).</summary>
    public double CookingTimeScale { get; set; } = 1;

    /// <summary>Ground item points (scripts/gen-item-spawns.py), beside the balance JSON.</summary>
    public string? ItemSpawnsPath => BalancePath is null ? null : Path.Combine(Path.GetDirectoryName(BalancePath)!, "item-spawns.json");

    /// <summary>Cooking recipes' ingredients (scripts/gen-cooking.py), beside the balance JSON.</summary>
    public string? CookingPath => BalancePath is null ? null : Path.Combine(Path.GetDirectoryName(BalancePath)!, "cooking.json");

    /// <summary>Fishing spots and catches (scripts/gen-fishing.py), beside the balance JSON.</summary>
    public string? FishingPath => BalancePath is null ? null : Path.Combine(Path.GetDirectoryName(BalancePath)!, "fishing.json");

    /// <summary>Official loot tables (scripts/import-grimoire.py), beside the balance JSON. Missing → approximated drops.</summary>
    public string? LootPath => BalancePath is null ? null : Path.Combine(Path.GetDirectoryName(BalancePath)!, "loot.json");

    /// <summary>Official monster levels and health (scripts/import-grimoire.py), beside the balance JSON.</summary>
    public string? MobStatsPath => BalancePath is null ? null : Path.Combine(Path.GetDirectoryName(BalancePath)!, "mob-stats.json");

    /// <summary>Status effect tick times and damage types (scripts/import-grimoire.py), beside the balance JSON.</summary>
    public string? StatusEffectsPath => BalancePath is null ? null : Path.Combine(Path.GetDirectoryName(BalancePath)!, "status-effects.json");

    /// <summary>Skill aggro per rank (scripts/import-grimoire.py), beside the balance JSON.</summary>
    public string? SkillAggroPath => BalancePath is null ? null : Path.Combine(Path.GetDirectoryName(BalancePath)!, "skill-aggro.json");

    /// <summary>Monster scripts (scripts/import-grimoire.py), beside the balance JSON.</summary>
    public string? MobScriptsPath => BalancePath is null ? null : Path.Combine(Path.GetDirectoryName(BalancePath)!, "mob-scripts.json");

    /// <summary>Monster skill sets and AI (scripts/import-grimoire.py), beside the balance JSON.</summary>
    public string? MobSkillsPath => BalancePath is null ? null : Path.Combine(Path.GetDirectoryName(BalancePath)!, "mob-skills.json");

    /// <summary>Skill damage types (scripts/import-grimoire.py), beside the balance JSON.</summary>
    public string? SkillTypesPath => BalancePath is null ? null : Path.Combine(Path.GetDirectoryName(BalancePath)!, "skill-types.json");

    /// <summary>Equipment sets (scripts/import-grimoire.py), beside the balance JSON.</summary>
    public string? ItemSetsPath => BalancePath is null ? null : Path.Combine(Path.GetDirectoryName(BalancePath)!, "item-sets.json");

    /// <summary>Armour set auras with the owner's values (server/content/set-bonuses.json, copied next to the server binary).</summary>
    public string? SetAurasPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "content", "set-bonuses.json");

    /// <summary>Zone exits (scripts/extract-zone-points.py), read from beside the balance JSON.</summary>
    public string? ZonePointsPath => BalancePath is null ? null : Path.Combine(Path.GetDirectoryName(BalancePath)!, "zone-points.json");

    /// <summary>Players connecting from this machine (the host) get GM commands. Others need <c>.gm &lt;name&gt;</c>.</summary>
    public bool LocalGm { get; set; } = true;

    /// <summary>Accounts made GM at start-up (<c>--gm name</c>, repeatable).</summary>
    public List<string> GmAccounts { get; } = new();

    /// <summary>Our own items (server/content/custom-items.json, copied next to the server binary).</summary>
    public string? CustomItemsPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "content", "custom-items.json");

    /// <summary>Our own fixed-price merchant stock (server/content/shop-extras.json, copied next to the server binary).</summary>
    public string? ShopExtrasPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "content", "shop-extras.json");

    /// <summary>The living calendar's daily track (server/content/calendar.json, copied next to the server binary).</summary>
    public string? CalendarPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "content", "calendar.json");

    /// <summary>Faction reputation sources (server/content/factions.json, copied next to the server binary).</summary>
    public string? FactionsPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "content", "factions.json");

    /// <summary>Combat ability gain chance (server/content/ability-gain.json, copied next to the server binary).</summary>
    public string? AbilityGainPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "content", "ability-gain.json");

    /// <summary>Overrides the file's combat ability gain chance (tests).</summary>
    public double? CombatAbilityChance { get; set; }

    /// <summary>The owner's skill changes (server/content/skill-overrides.json, copied next to the server binary).</summary>
    public string? SkillOverridesPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "content", "skill-overrides.json");

    /// <summary>Health and energy regeneration rules (server/content/regen.json, copied next to the server binary).</summary>
    public string? RegenPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "content", "regen.json");

    /// <summary>Each class's starting skills and its abilities (server/content/class-kits.json).</summary>
    public string? ClassKitsPath { get; set; } = DefaultClassKitsPath;

    /// <summary>Where <see cref="ClassKitsPath"/> starts; the tests clear it so they use the built-in kits, not the owner's.</summary>
    public static string? DefaultClassKitsPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "content", "class-kits.json");

    /// <summary>XP from each level to the next (server/content/xp-levels.json, the live game's table).</summary>
    public string? XpLevelsPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "content", "xp-levels.json");

    /// <summary>Whether monsters attack players on sight (server/content/aggro.json; off by owner's rule).</summary>
    public string? AggroPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "content", "aggro.json");

    /// <summary>Overrides aggro.json when set (tests).</summary>
    public bool? AggroOnSight { get; set; }

    /// <summary>Monster model sizes by appearance (server/content/mob-scales.json, copied next to the server binary).</summary>
    public string? MobScalesPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "content", "mob-scales.json");

    /// <summary>The platinum item shop catalogue (server/content/item-shop.json, copied next to the server binary).</summary>
    public string? ItemShopPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "content", "item-shop.json");

    /// <summary>The passive tree (server/content/passive-tree.json, copied next to the server binary).</summary>
    public string? PassiveTreePath { get; set; } = Path.Combine(AppContext.BaseDirectory, "content", "passive-tree.json");

    /// <summary>
    /// The passive ("progression") tree, our own addition. Off by default (<c>--passive-tree</c> turns it on) while the
    /// base game systems are brought in line with the official data: its bonuses would skew every number being checked.
    /// Players' chosen nodes stay saved while it is off.
    /// </summary>
    public bool PassiveTreeEnabled { get; set; }

    public void Validate()
    {
        if (!IPAddress.IsLoopback(BindAddress) && !AllowRemote)
            throw new InvalidOperationException($"Refusing to bind {BindAddress}: not loopback. Pass --allow-remote to expose the server deliberately.");
    }

    public static ServerOptions FromArgs(string[] args)
    {
        var o = new ServerOptions();
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            switch (args[i])
            {
                case "--http-port": o.HttpPort = int.Parse(Next()); break;
                case "--game-port": o.GamePort = int.Parse(Next()); break;
                case "--bind": o.BindAddress = IPAddress.Parse(Next()); break;
                case "--public-host": o.PublicHost = Next(); break;
                case "--data": o.DataDirectory = Path.GetFullPath(Next()); break;
                case "--allow-remote": o.AllowRemote = true; break;
                case "--tailscale":
                    var ts = NetworkAccess.TailscaleAddress()
                        ?? throw new ArgumentException("--tailscale: this machine has no Tailscale address. Start Tailscale and log in first.");
                    o.TailscaleOnly = true;
                    o.AllowRemote = true;
                    o.BindAddress = IPAddress.Any;
                    o.PublicHost = ts.ToString();
                    break;
                case "--balance": o.BalancePath = Path.GetFullPath(Next()); break;
                case "--no-balance": o.BalancePath = null; break;
                case "--gm": o.GmAccounts.Add(Next()); break;
                case "--no-local-gm": o.LocalGm = false; break;
                case "--passive-tree": o.PassiveTreeEnabled = true; break;
                case "--client-port": o.ClientHttpPort = int.Parse(Next()); break;
                default: throw new ArgumentException($"unknown option {args[i]}");
            }
        }
        return o;
    }
}
