using CH.Patcher;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CH.Tests;

/// <summary>
/// Runs the patch set against the pristine client assembly from the local snapshot. The proprietary DLL is never in the
/// repo, so on a machine without the snapshot these tests pass without checking anything (they print why).
/// </summary>
public class PatcherTests
{
    private static readonly string Managed = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "CelticHeroesAnalysis", "snapshots", "24928510", "Contents", "Resources", "Data", "Managed");

    private static ModuleDefinition? LoadPristine()
    {
        var dll = Path.Combine(Managed, "Assembly-CSharp.dll");
        if (!File.Exists(dll)) return null;
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Managed);
        return ModuleDefinition.ReadModule(dll, new ReaderParameters { AssemblyResolver = resolver });
    }

    [Fact]
    public void Patch_set_applies_with_pinned_site_counts_and_leaves_no_live_urls()
    {
        using var module = LoadPristine();
        if (module is null) return; // snapshot not present on this machine

        var patcher = new AssemblyPatcher(module, 5555);
        patcher.ApplyAll(); // throws if any site count differs from build 24928510
        Assert.Equal(["P2", "P4", "P4b", "P5", "P6a", "P6b", "P7", "P10", "P11", "P13", "P12", "P14", "P15"], patcher.Results.Select(r => r.Id));
        Assert.Empty(patcher.RemainingLiveUrls());
    }

    [Fact]
    public void Patched_assembly_round_trips_and_environment_points_at_localhost()
    {
        using var module = LoadPristine();
        if (module is null) return;

        new AssemblyPatcher(module, 6000).ApplyAll();
        using var buffer = new MemoryStream();
        module.Write(buffer);
        buffer.Position = 0;
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Managed);
        using var reloaded = ModuleDefinition.ReadModule(buffer, new ReaderParameters { AssemblyResolver = resolver });

        var env = reloaded.GetType("EnvironmentSetup");
        var literals = env.Methods.Where(m => m.HasBody).SelectMany(m => m.Body.Instructions)
            .Where(i => i.OpCode == OpCodes.Ldstr).Select(i => (string)i.Operand).ToList();
        Assert.Contains("http://127.0.0.1:6000/patchserver/PatchServerURL.aspx?iav={0}&platform={1}", literals);
        Assert.DoesNotContain(env.Methods.Where(m => m.HasBody).SelectMany(m => m.Body.Instructions),
            i => i.OpCode == OpCodes.Ldfld && i.Operand is FieldReference { Name: "remoteEnvironmentRequestUrl" or "defaultPatchServerUrl" or "defaultBalanceCdnUrlFormat" });

        var calls = reloaded.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody).SelectMany(m => m.Body.Instructions)
            .Where(i => i.Operand is MethodReference).Select(i => (MethodReference)i.Operand)
            .Select(m => $"{m.DeclaringType.FullName}::{m.Name}").ToHashSet();
        Assert.DoesNotContain("SharedTech.Services::Init", calls);
        Assert.DoesNotContain("Unity.Services.Core.UnityServices::InitializeAsync", calls);
        Assert.DoesNotContain("UnityEngine.Purchasing.UnityPurchasing::Initialize", calls);
        Assert.DoesNotContain(calls, c => c.Contains("ZendeskSdk") && c.EndsWith("::InitializeAsync"));
    }

    [Fact]
    public void Edited_methods_have_valid_branch_targets_after_round_trip()
    {
        using var module = LoadPristine();
        if (module is null) return;

        new AssemblyPatcher(module, 5555).ApplyAll();
        using var buffer = new MemoryStream();
        module.Write(buffer);
        buffer.Position = 0;
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Managed);
        using var reloaded = ModuleDefinition.ReadModule(buffer, new ReaderParameters { AssemblyResolver = resolver });

        // The first real-client run hit InvalidProgramException (a short branch pushed out of range). Check every branch.
        var touched = new[] { "EnvironmentSetup", "CController", "InAppPurchaseManager", "CUtilities" };
        foreach (var type in reloaded.GetTypes().Where(t => touched.Contains(t.FullName) || touched.Contains(t.DeclaringType?.FullName)))
        foreach (var method in type.Methods.Where(m => m.HasBody))
        {
            var instructions = method.Body.Instructions.ToHashSet();
            foreach (var ins in method.Body.Instructions)
            {
                if (ins.Operand is Instruction target)
                {
                    Assert.Contains(target, instructions);
                    if (ins.OpCode.OperandType == OperandType.ShortInlineBrTarget)
                        Assert.InRange(target.Offset - (ins.Offset + ins.GetSize()), sbyte.MinValue, sbyte.MaxValue);
                }
                else if (ins.Operand is Instruction[] targets)
                {
                    Assert.All(targets, t => Assert.Contains(t, instructions));
                }
            }
        }
    }

    [Theory]
    [InlineData("https://cdp.cloud.unity3d.com/v1/events")]
    [InlineData("https://dashboard.unity3d.com")]
    [InlineData("https://engine-data.services.api.unity.com/v1/stack-traces")]
    public void Unity_urls_are_rewritten_to_localhost_at_the_same_length(string url)
    {
        var replacement = AssetUrlPatcher.SameLength(url, 5555);
        Assert.Equal(url.Length, replacement.Length);
        Assert.StartsWith("http://127.0.0.1:5555/u/", replacement);
        Assert.Empty(AssetUrlPatcher.RemainingUnityUrls(System.Text.Encoding.ASCII.GetBytes(replacement)));
    }

    [Fact]
    public void Asset_patch_keeps_length_and_surrounding_bytes()
    {
        var asset = System.Text.Encoding.ASCII.GetBytes("\u0027\0\0\0https://cdp.cloud.unity3d.com/v1/events\0\0XYZ");
        var (patched, changes) = AssetUrlPatcher.Patch(asset, 5555);
        Assert.Single(changes);
        Assert.Equal(asset.Length, patched.Length);
        Assert.Equal(asset[..4], patched[..4]);
        Assert.Equal(asset[^5..], patched[^5..]);
    }

    [Fact]
    public void P9_renames_the_company_once_at_the_same_length()
    {
        var ggm = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "CelticHeroesAnalysis", "client", "24928510", "pristine", "globalgamemanagers"); // CH.Patcher's hash-checked backup
        if (!File.Exists(ggm)) return;
        var bytes = File.ReadAllBytes(ggm);
        var length = bytes.Length;
        Assert.Equal(1, CH.Patcher.AssetUrlPatcher.PatchCompanyName(bytes));
        Assert.Equal(length, bytes.Length);
        var text = System.Text.Encoding.Latin1.GetString(bytes);
        Assert.Contains(CH.Patcher.AssetUrlPatcher.PrivateCompanyName, text);
        Assert.DoesNotContain(CH.Patcher.AssetUrlPatcher.LiveCompanyName, text);
        Assert.Equal(0, CH.Patcher.AssetUrlPatcher.PatchCompanyName(bytes)); // idempotent: nothing left to rename
    }
}

/// <summary>
/// The Windows build is obfuscated: the patch set and the client mod go through its name map (docs/windows-names.md).
/// Needs the downloaded Windows depot and the host's generated map; passes without checking otherwise.
/// </summary>
public class WindowsBuildTests
{
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static readonly string Depot = Path.Combine(Home, "Library", "Application Support", "Steam", "Steam.AppBundle", "Steam", "Contents", "MacOS",
        "steamapps", "content", "app_2829820", "depot_2829821");
    private static readonly string Dll = Path.Combine(Depot, "CelticHeroes_Data", "Managed", "Assembly-CSharp.dll");
    private static readonly string MacDll = Path.Combine(Home, "CelticHeroesAnalysis", "snapshots", "24928510", "Contents", "Resources", "Data", "Managed", "Assembly-CSharp.dll");

    private static (ModuleDefinition Module, NameMap Names)? Load()
    {
        if (!File.Exists(Dll)) return null;
        var known = KnownBuild.All.Single(b => b.Platform == ClientPlatform.Windows);
        var names = NameMap.Embedded(known.DllSha256);
        if (names is null) return null;
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(Dll)!);
        return (ModuleDefinition.ReadModule(new MemoryStream(File.ReadAllBytes(Dll)), new ReaderParameters { AssemblyResolver = resolver }), names);
    }

    [Fact]
    public void The_patch_set_applies_to_the_obfuscated_build_with_the_same_site_counts()
    {
        if (Load() is not var (module, names)) return;
        using var _ = module;
        var patcher = new AssemblyPatcher(module, 5555, "100.64.0.1", names);
        patcher.ApplyAll(); // throws if any real site count differs from the Mac build's
        Assert.Equal(13, patcher.Results.Count);
        Assert.Empty(patcher.RemainingLiveUrls()); // decoy copies included
    }

    [Fact]
    public void The_matcher_reproduces_the_shipped_map()
    {
        if (!File.Exists(Dll) || !File.Exists(MacDll) || Load() is not var (win, shipped)) return;
        using var _ = win;
        using var mac = ModuleDefinition.ReadModule(MacDll);
        var built = new NameMatcher(mac, win).Build();
        Assert.Equal(shipped.Types.Count, built.Types.Count);
        Assert.Equal(shipped.Methods.Count, built.Methods.Count);
        Assert.Equal("CKGBDDIKPME", built.Types["NetworkManager"]);
        Assert.Equal("LCIMANMMKCC", built.Members["NetworkManager::sendChatMessage/3"]);
    }

    /// <summary>Every member the client mod looks up by name (CHPrivateMods.R), resolved as the mod resolves it.</summary>
    [Theory]
    [InlineData("CSkillPopup", "m_btnUpgrade", -1)]
    [InlineData("CSkillPopup", "SetLevelToUpgradeTo", 1)]
    [InlineData("CSkillPopup", "ConfirmButtonPressed", 0)]
    [InlineData("Lidgren.Network.NetIncomingMessage", "Position", -2)]
    [InlineData("Lidgren.Network.NetIncomingMessage", "ReadVariableUInt32", 0)]
    [InlineData("Lidgren.Network.NetIncomingMessage", "ReadVariableInt32", 0)]
    [InlineData("Lidgren.Network.NetIncomingMessage", "ReadString", 0)]
    [InlineData("Lidgren.Network.NetIncomingMessage", "ReadByte", 0)]
    [InlineData("CController", "instance", -1)]
    [InlineData("CController", "m_networkManager", -1)]
    [InlineData("NetworkManager", "sendChatMessage", 3)]
    [InlineData("GUIManager", "instance", -1)]
    [InlineData("GUIManager", "htmlWindowOpen", -1)]
    [InlineData("Assets.Main.HUD.HUDOptionsScreen", "statsButton", -1)]
    [InlineData("Assets.Main.HUD.HUDOptionsScreen", "freePlatButton", -1)]
    [InlineData("CMD5_Model", "m_modelIDs", -1)]
    [InlineData("CMD5_Model", "m_modelComponents", -1)]
    [InlineData("CModelIDs", "m_visibleEquipmentTemplates", -1)]
    [InlineData("ModelComponent", "m_slotType", -1)]
    [InlineData("ModelComponent", "m_gameObject", -1)]
    [InlineData("ModelComponent", "m_material", -1)]
    [InlineData("ModelComponent", "effectMesh", -1)]
    [InlineData("ModelComponent", "PrimaryColour", -2)]
    [InlineData("Balance.Model.BalanceClient", "ItemTemplates", -1)]
    [InlineData("Balance.Model.BalanceClient", "SkillTemplates", -1)]
    [InlineData("Balance.Model.BalanceClient", "EquipmentModelIDTemplates", -1)]
    [InlineData("Balance.Model.Templates.Skills.Client.SkillTemplateLevel", "InitialDamage", -1)]
    [InlineData("Balance.Model.Templates.Visual.ModelIDParticleComponent", "AttachBone1", -1)]
    public void The_client_mod_finds_its_members_in_the_obfuscated_build(string macType, string member, int arity)
    {
        if (Load() is not var (module, names)) return;
        using var _ = module;
        var lines = names.ModLines().Select(l => l.Split('|')).ToList();
        var types = lines.Where(p => p[0] == "T").ToDictionary(p => p[1], p => p[2]);
        var back = types.ToDictionary(kv => kv.Value, kv => kv.Key);
        var members = lines.Where(p => p[0] == "M").ToDictionary(p => p[1], p => p[2]);
        var type = module.GetType(types.GetValueOrDefault(macType, macType));
        Assert.NotNull(type);
        for (var t = type; t is not null; t = t.BaseType?.Resolve())
        {
            if (t.Module != module) break;
            var mac = back.GetValueOrDefault(t.FullName, t.FullName);
            var key = $"{mac}::{member}{(arity >= 0 ? $"/{arity}" : "")}";
            var name = members.GetValueOrDefault(key, member);
            var found = arity == -1 ? t.Fields.Any(f => f.Name == name)
                : arity == -2 ? t.Properties.Any(p => p.Name == name)
                : t.Methods.Any(m => m.Name == name && m.Parameters.Count == arity);
            if (found) return;
        }
        Assert.Fail($"{macType}::{member} not found in the Windows build");
    }
}
