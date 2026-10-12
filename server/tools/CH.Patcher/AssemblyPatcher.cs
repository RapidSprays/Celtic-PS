using System.Text.RegularExpressions;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace CH.Patcher;

/// <summary>One applied patch: manifest id, what changed, and how many IL sites it touched.</summary>
public sealed record PatchResult(string Id, string Description, int Sites);

/// <summary>
/// Applies the private-client patch set (docs/patch-manifest.md) to a pristine Assembly-CSharp. Every patch states how
/// many sites it expects in build 24928510 and fails if it finds a different number, so a changed client build stops
/// the patcher instead of being half-patched.
/// </summary>
public sealed class AssemblyPatcher
{
    /// <summary>URL literals pointing at live DECA / Celtic Heroes hosts.</summary>
    public static readonly Regex LiveHostUrl = new(@"^https?://([a-z0-9-]+\.)*(decagames\.com|celtic-heroes\.com)(:\d+)?(/|$)", RegexOptions.IgnoreCase);

    private readonly ModuleDefinition _module;
    private readonly int _httpPort;
    private readonly string _host;
    private readonly List<PatchResult> _results = new();
    private readonly HashSet<MethodDefinition> _edited = new();

    /// <param name="host">the private server's address: 127.0.0.1 on the host's own machine, its Tailscale IP for friends</param>
    public AssemblyPatcher(ModuleDefinition module, int httpPort, string host = "127.0.0.1")
    {
        _module = module;
        _httpPort = httpPort;
        _host = host;
    }

    public IReadOnlyList<PatchResult> Results => _results;

    private string Local(string path) => $"http://{_host}:{_httpPort}/{path}";

    public void ApplyAll()
    {
        // P2/P3: the environment URL and the fallback URLs are serialized fields on the EnvironmentSetup asset. Replace
        // every read of them with a localhost constant, so the asset's production values are never used.
        ReplaceFieldReads("P2", "EnvironmentSetup", new Dictionary<string, string>
        {
            ["remoteEnvironmentRequestUrl"] = Local("patchserver/PatchServerURL.aspx?iav={0}&platform={1}"),
            ["defaultPatchServerUrl"] = Local("patchserver/"),
            ["defaultBalanceCdnUrlFormat"] = Local("patchserver/balancefiles/{0}/balance-client.json"),
            ["localPatchServerUrl"] = Local("patchserver/"),
            ["localBalanceCdnUrlFormat"] = Local("patchserver/balancefiles/{0}/balance-client.json"),
        }, expectedSites: 6);

        // P4: remaining hard-coded live URLs (server status popup, support link) → our HTTP server's /external/ path,
        // which logs what the client asked for.
        RewriteLiveUrlLiterals("P4", expectedSites: 3);

        // P4b: the connectivity check (NetworkManager.CheckInternetSettings) fetches google.com; point it at our /ping.
        RewriteLiteral("P4b", "NetworkManager", "CheckInternetSettings", "http://www.google.com", Local("ping"), expectedSites: 1);

        // P5: SharedTech Services.Init (Firebase analytics, Crashlytics, messaging) is skipped entirely.
        NeutralizeCalls("P5", "CController", "Awake", "SharedTech.Services::Init", expectedSites: 1);

        // P6: Unity Gaming Services and the Zendesk SDK (with its continuation) are skipped.
        NeutralizeCalls("P6a", "CController", "Start", "Unity.Services.Core.UnityServices::InitializeAsync", expectedSites: 1);
        NeutralizeZendesk("P6b");

        // P7: Unity IAP never initializes, so the store and purchases are unavailable.
        NeutralizeCalls("P7", "InAppPurchaseManager", ".ctor", "UnityEngine.Purchasing.UnityPurchasing::Initialize", expectedSites: 1);

        // P10: a "Max" button on the skill popup (CHPrivateMods.dll, shipped next to Assembly-CSharp).
        InjectMaxButton("P10");

        // P11: our own items (server/content/custom-items.json) join the client's item templates.
        InjectModCall("P11", "CItemManager", "Init", argument: 1, modType: "CustomItems", modMethod: "Register", after: null);

        // P13: custom items' own models (a weapon model plus fashion particle effects), before the client builds its model list.
        InjectModCall("P13", "CModelIDTemplateManager", "Init", argument: 1, modType: "CustomItems", modMethod: "RegisterModels", after: null);

        // P12: the rainbow effect on weapons, once a character model has finished loading.
        InjectModCall("P12", "CMD5_Model", "checkDownloadComplete", argument: 0, modType: "RainbowWeapons", modMethod: "OnModelLoaded",
            after: ins => ins.OpCode == OpCodes.Stfld && ins.Operand is FieldReference { Name: "m_isFullyLoaded" } && ins.Previous?.OpCode == OpCodes.Ldc_I4_1);

        // P14: the passive tree panel reads the server's own messages (TreeLayout/TreeState) before the client's switch.
        InjectModCall("P14", "NetworkManager", "processCommand", argument: 1, modType: "PassiveTree", modMethod: "OnMessage", after: null);

        // P15: a "Passives" button in the HUD options menu, after Init's last line (free platinum's click listener), so the
        // game's own listeners are all in place before the mod takes a button over.
        InjectModCall("P15", "Assets.Main.HUD.HUDOptionsScreen", "Init", argument: 0, modType: "PassiveTree", modMethod: "AttachMenuButton",
            after: ins => ins.OpCode == OpCodes.Callvirt && ins.Operand is MethodReference { Name: "AddListener" }
                && ins.Previous?.Previous?.Operand is MethodReference { Name: "OnFreePlatButtonPressed" });

        // Inserting instructions can push short-form branches out of range; re-pick branch sizes for every edited method.
        foreach (var method in _edited) method.Body.OptimizeMacros();
    }

    /// <summary>Call before changing a method body: expands short branches so offsets can grow safely.</summary>
    private ILProcessor BeginEdit(MethodDefinition method)
    {
        if (_edited.Add(method)) method.Body.SimplifyMacros();
        return method.Body.GetILProcessor();
    }

    /// <summary>Lists every URL literal in the module that still points at a live host. Must be empty after patching.</summary>
    public IReadOnlyList<string> RemainingLiveUrls() =>
        AllInstructions()
            .Where(x => x.Ins.OpCode == OpCodes.Ldstr && x.Ins.Operand is string s && LiveHostUrl.IsMatch(s))
            .Select(x => $"{x.Method.FullName}: {x.Ins.Operand}")
            .ToList();

    private void ReplaceFieldReads(string id, string typeName, IReadOnlyDictionary<string, string> values, int expectedSites)
    {
        var type = _module.GetType(typeName) ?? throw new PatchException(id, $"type {typeName} not found");
        var sites = 0;
        foreach (var (method, ins) in AllInstructions().ToList())
        {
            if (ins.OpCode != OpCodes.Ldfld || ins.Operand is not FieldReference f || f.DeclaringType.FullName != type.FullName) continue;
            if (!values.TryGetValue(f.Name, out var value)) continue;
            // ldfld consumes the instance and pushes the string: pop the instance, push the constant instead. The original
            // instruction becomes the pop so any branch that targeted it still lands on a balanced sequence.
            var il = BeginEdit(method);
            ins.OpCode = OpCodes.Pop;
            ins.Operand = null;
            il.InsertAfter(ins, il.Create(OpCodes.Ldstr, value));
            sites++;
        }
        Record(id, $"{typeName}: reads of {string.Join(", ", values.Keys)} replaced with private-server URLs", sites, expectedSites);
    }

    private void RewriteLiveUrlLiterals(string id, int expectedSites)
    {
        var sites = 0;
        foreach (var (_, ins) in AllInstructions())
        {
            if (ins.OpCode != OpCodes.Ldstr || ins.Operand is not string s || !LiveHostUrl.IsMatch(s)) continue;
            var uri = new Uri(s);
            ins.Operand = Local($"external/{uri.Host}{uri.PathAndQuery}");
            sites++;
        }
        Record(id, $"live URL literals rewritten to http://{_host}:<port>/external/<host><path>", sites, expectedSites);
    }

    private void RewriteLiteral(string id, string typeName, string methodName, string from, string to, int expectedSites)
    {
        var sites = 0;
        foreach (var (method, ins) in AllInstructions())
        {
            if (!InScope(method, typeName, methodName) || ins.OpCode != OpCodes.Ldstr || (string)ins.Operand != from) continue;
            ins.Operand = to;
            sites++;
        }
        Record(id, $"{typeName}.{methodName}: \"{from}\" → {to}", sites, expectedSites);
    }

    /// <summary>
    /// Replaces each matching call with pops of its arguments (and instance), pushing null for a reference-type result.
    /// <paramref name="methodName"/> may name a compiler-generated state machine's owner (matched by prefix).
    /// </summary>
    private void NeutralizeCalls(string id, string typeName, string methodName, string calleePrefix, int expectedSites)
    {
        var sites = 0;
        foreach (var (method, ins) in AllInstructions().ToList())
        {
            if (!InScope(method, typeName, methodName)) continue;
            if (ins.OpCode != OpCodes.Call && ins.OpCode != OpCodes.Callvirt) continue;
            if (ins.Operand is not MethodReference callee || !$"{callee.DeclaringType.FullName}::{callee.Name}".StartsWith(calleePrefix, StringComparison.Ordinal)) continue;
            Neutralize(method, ins, callee);
            sites++;
        }
        Record(id, $"{typeName}.{methodName}: calls to {calleePrefix} removed", sites, expectedSites);
    }

    private void NeutralizeZendesk(string id)
    {
        var sites = 0;
        foreach (var (method, ins) in AllInstructions().ToList())
        {
            if (ins.Operand is not MethodReference callee || callee.Name != "InitializeAsync" || !callee.DeclaringType.FullName.Contains("ZendeskSdk")) continue;
            Neutralize(method, ins, callee);
            // Its result feeds Task.ContinueWith(...) and then a pop. Remove that continuation call too.
            var next = ins.Next;
            while (next is not null && !(next.Operand is MethodReference m && m.Name == "ContinueWith")) next = next.Next;
            if (next is null) throw new PatchException(id, "ContinueWith after ZendeskSdk.InitializeAsync not found");
            Neutralize(method, next, (MethodReference)next.Operand);
            sites++;
        }
        Record(id, "CController.Init: ZendeskSdk.InitializeAsync(...).ContinueWith(...) removed", sites, expected: 1);
    }

    /// <summary>Name of the mod assembly P10 calls into (built from tools/CH.ClientMods).</summary>
    public const string ModsAssembly = "CHPrivateMods";

    /// <summary>
    /// P10: at the end of CSkillPopup's constructor, call CHPrivateMods.SkillMaxButton.Attach(this). The call sits in
    /// its own small method invoked inside try/catch, so if the mod assembly were missing (the JIT fails resolving it
    /// when compiling that small method) the popup still opens normally.
    /// </summary>
    private void InjectMaxButton(string id)
    {
        var popup = _module.GetType("CSkillPopup");
        var ctors = popup?.Methods.Where(m => m.IsConstructor && !m.IsStatic && m.Parameters.Count == 5).ToList() ?? [];
        var sites = 0;
        if (ctors.Count == 1 && popup!.Methods.All(m => m.Name != "CHPrivate_AttachMax"))
        {
            var mods = new AssemblyNameReference(ModsAssembly, new Version(1, 0, 0, 0));
            if (_module.AssemblyReferences.All(r => r.Name != ModsAssembly)) _module.AssemblyReferences.Add(mods);
            var modType = new TypeReference("CHPrivateMods", "SkillMaxButton", _module, _module.AssemblyReferences.First(r => r.Name == ModsAssembly));
            var attach = new MethodReference("Attach", _module.TypeSystem.Void, modType) { HasThis = false };
            attach.Parameters.Add(new ParameterDefinition(_module.TypeSystem.Object));

            var helper = new MethodDefinition("CHPrivate_AttachMax", MethodAttributes.Private | MethodAttributes.HideBySig, _module.TypeSystem.Void);
            var h = helper.Body.GetILProcessor();
            h.Emit(OpCodes.Ldarg_0);
            h.Emit(OpCodes.Call, attach);
            h.Emit(OpCodes.Ret);
            popup.Methods.Add(helper);

            var ctor = ctors[0];
            var il = BeginEdit(ctor);
            var ret = ctor.Body.Instructions.Last();
            if (ret.OpCode != OpCodes.Ret) throw new PatchException(id, "CSkillPopup constructor doesn't end in ret");
            var tryStart = il.Create(OpCodes.Ldarg_0);
            var call = il.Create(OpCodes.Call, helper);
            var leaveTry = il.Create(OpCodes.Leave, ret);
            var catchStart = il.Create(OpCodes.Pop);
            var leaveCatch = il.Create(OpCodes.Leave, ret);
            foreach (var ins in new[] { tryStart, call, leaveTry, catchStart, leaveCatch }) il.InsertBefore(ret, ins);
            // Earlier "return" branches to ret still skip the Max button only in paths that never built the popup.
            ctor.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
            {
                TryStart = tryStart, TryEnd = catchStart, HandlerStart = catchStart, HandlerEnd = ret,
                CatchType = new TypeReference("System", "Exception", _module, _module.TypeSystem.CoreLibrary),
            });
            sites = 1;
        }
        Record(id, "CSkillPopup..ctor: Max button (CHPrivateMods.SkillMaxButton.Attach)", sites, expected: 1);
    }

    /// <summary>
    /// Calls <c>CHPrivateMods.&lt;modType&gt;.&lt;modMethod&gt;(object)</c> with the method's argument <paramref name="argument"/>
    /// (0 = this), at the start of the method or right after the instruction <paramref name="after"/> matches (exactly
    /// one site). Like P10, the call goes through a small static helper inside try/catch, so the game is unaffected if
    /// the mod is missing or throws.
    /// </summary>
    private void InjectModCall(string id, string typeName, string methodName, int argument, string modType, string modMethod,
        Func<Instruction, bool>? after)
    {
        var type = _module.GetType(typeName);
        var targets = type?.Methods.Where(m => m.Name == methodName && m.HasBody && m.Parameters.Count >= (argument == 0 ? 0 : argument)).ToList() ?? [];
        var helperName = $"CHPrivate_{modType}_{modMethod}";
        var sites = 0;
        if (targets.Count == 1 && type!.Methods.All(m => m.Name != helperName))
        {
            var target = targets[0];
            var anchors = after is null ? [target.Body.Instructions[0]] : target.Body.Instructions.Where(after).Select(i => i.Next).ToList();
            if (anchors.Count == 1 && anchors[0] is { } next)
            {
                if (_module.AssemblyReferences.All(r => r.Name != ModsAssembly))
                    _module.AssemblyReferences.Add(new AssemblyNameReference(ModsAssembly, new Version(1, 0, 0, 0)));
                var modTypeRef = new TypeReference("CHPrivateMods", modType, _module, _module.AssemblyReferences.First(r => r.Name == ModsAssembly));
                var modRef = new MethodReference(modMethod, _module.TypeSystem.Void, modTypeRef) { HasThis = false };
                modRef.Parameters.Add(new ParameterDefinition(_module.TypeSystem.Object));

                var helper = new MethodDefinition(helperName, MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig, _module.TypeSystem.Void);
                helper.Parameters.Add(new ParameterDefinition("arg", ParameterAttributes.None, _module.TypeSystem.Object));
                var h = helper.Body.GetILProcessor();
                h.Emit(OpCodes.Ldarg_0);
                h.Emit(OpCodes.Call, modRef);
                h.Emit(OpCodes.Ret);
                type.Methods.Add(helper);

                var il = BeginEdit(target);
                var load = argument == 0 ? il.Create(OpCodes.Ldarg_0) : il.Create(OpCodes.Ldarg, target.Parameters[argument - 1]);
                var argType = argument == 0 ? (TypeReference)target.DeclaringType : target.Parameters[argument - 1].ParameterType;
                var block = new List<Instruction> { load };
                if (argType.IsValueType) block.Add(il.Create(OpCodes.Box, argType));
                var call = il.Create(OpCodes.Call, helper);
                var leaveTry = il.Create(OpCodes.Leave, next);
                var catchStart = il.Create(OpCodes.Pop);
                var leaveCatch = il.Create(OpCodes.Leave, next);
                block.AddRange([call, leaveTry, catchStart, leaveCatch]);
                foreach (var ins in block) il.InsertBefore(next, ins);
                target.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
                {
                    TryStart = load, TryEnd = catchStart, HandlerStart = catchStart, HandlerEnd = next,
                    CatchType = new TypeReference("System", "Exception", _module, _module.TypeSystem.CoreLibrary),
                });
                sites = 1;
            }
        }
        Record(id, $"{typeName}.{methodName}: calls CHPrivateMods.{modType}.{modMethod}", sites, expected: 1);
    }

    private void Neutralize(MethodDefinition method, Instruction call, MethodReference callee)
    {
        var il = BeginEdit(method);
        var pops = callee.Parameters.Count + (callee.HasThis ? 1 : 0);
        var returnsValue = callee.ReturnType.MetadataType != MetadataType.Void;
        if (returnsValue && callee.ReturnType.IsValueType)
            throw new PatchException("neutralize", $"{callee.FullName} returns a value type; not supported");

        // Reuse the call instruction itself as the first replacement so branch targets stay valid.
        var replacement = new List<Instruction>();
        for (var i = 0; i < pops; i++) replacement.Add(il.Create(OpCodes.Pop));
        if (returnsValue) replacement.Add(il.Create(OpCodes.Ldnull));
        if (replacement.Count == 0) replacement.Add(il.Create(OpCodes.Nop));

        call.OpCode = replacement[0].OpCode;
        call.Operand = null;
        var anchor = call;
        foreach (var extra in replacement.Skip(1))
        {
            il.InsertAfter(anchor, extra);
            anchor = extra;
        }
    }

    private static bool InScope(MethodDefinition method, string typeName, string methodName)
    {
        var t = method.DeclaringType;
        // Coroutines and async methods compile to nested <Name>d__N types.
        if (t.DeclaringType is { } outer && t.Name.StartsWith($"<{methodName}>", StringComparison.Ordinal))
            return outer.FullName == typeName;
        return t.FullName == typeName && method.Name == methodName;
    }

    private IEnumerable<(MethodDefinition Method, Instruction Ins)> AllInstructions() =>
        from type in _module.GetTypes()
        from method in type.Methods
        where method.HasBody
        from ins in method.Body.Instructions
        select (method, ins);

    /// <summary>When set, site-count mismatches are reported instead of failing (used to pin counts for a new build).</summary>
    public bool Discover { get; init; }

    private void Record(string id, string description, int sites, int expected)
    {
        if (Discover)
        {
            _results.Add(new PatchResult(id, $"{description} [expected {expected}]", sites));
            return;
        }
        if (sites != expected)
            throw new PatchException(id, $"expected {expected} site(s), found {sites}. The client build differs from 24928510; re-check docs/patch-manifest.md before patching.");
        _results.Add(new PatchResult(id, description, sites));
    }
}

public sealed class PatchException(string id, string message) : Exception($"[{id}] {message}");
