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
        Assert.Equal(["P2", "P4", "P4b", "P5", "P6a", "P6b", "P7"], patcher.Results.Select(r => r.Id));
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
}
