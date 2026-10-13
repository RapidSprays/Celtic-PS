using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CH.Patcher;

/// <summary>
/// Pairs the Windows build's obfuscated Assembly-CSharp with the Mac build's readable one by <b>code shape</b>
/// (docs/windows-names.md). The obfuscator renames types and members and adds decoy methods built from scraps of real
/// code, but leaves real method bodies instruction-for-instruction intact. So:
/// <list type="number">
/// <item>every method gets a fingerprint: its instructions with names of this assembly's own types and members blanked
/// out, while strings, numbers, branch shapes and references to other assemblies (Unity, .NET) stay;</item>
/// <item>a fingerprint found exactly once on each side pairs those two methods (decoys don't share real fingerprints);</item>
/// <item>paired methods are walked side by side: the members and types they use at the same instruction are pairs too,
/// which resolves short or repeated methods the fingerprint alone can't; repeat until nothing new;</item>
/// <item>types follow their methods; enum values pair by value; properties by their paired accessors.</item>
/// </list>
/// Anything that would pair one thing with two is dropped, so the patcher's pinned site counts catch a gap rather than
/// a wrong match.
/// </summary>
public sealed class NameMatcher
{
    private readonly ModuleDefinition _mac, _win;
    private readonly Dictionary<MethodDefinition, MethodDefinition> _methods = new();
    private readonly Dictionary<MethodDefinition, MethodDefinition> _methodsBack = new();
    private readonly Dictionary<TypeDefinition, TypeDefinition> _types = new();
    private readonly Dictionary<TypeDefinition, TypeDefinition> _typesBack = new();
    private readonly Dictionary<FieldDefinition, FieldDefinition> _fields = new();
    private readonly Dictionary<FieldDefinition, FieldDefinition> _fieldsBack = new();
    private readonly HashSet<object> _conflicted = new();
    private readonly Queue<(MethodDefinition Mac, MethodDefinition Win)> _toWalk = new();

    /// <summary>Methods shorter than this are too generic to pair on fingerprint alone (they still pair by walking).</summary>
    private const int MinSeedInstructions = 8;

    public NameMatcher(ModuleDefinition mac, ModuleDefinition win)
    {
        _mac = mac;
        _win = win;
    }

    public string Report { get; private set; } = "";

    public NameMap Build()
    {
        var macMethods = Bodies(_mac);
        var winMethods = Bodies(_win);
        var macPrints = macMethods.ToDictionary(m => m, Fingerprint);
        var winPrints = winMethods.ToDictionary(m => m, Fingerprint);

        // Types whose names survived (MonoBehaviours, data classes) pair by name.
        var winByName = _win.GetTypes().GroupBy(t => t.FullName).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.First());
        foreach (var t in _mac.GetTypes())
            if (winByName.TryGetValue(t.FullName, out var w) && !LooksObfuscated(t.Name)) PairType(t, w);

        var macGroups = macPrints.GroupBy(kv => kv.Value).ToDictionary(g => g.Key, g => g.Select(kv => kv.Key).ToList());
        var winGroups = winPrints.GroupBy(kv => kv.Value).ToDictionary(g => g.Key, g => g.Select(kv => kv.Key).ToList());

        // Seeds: unique fingerprints of real substance.
        foreach (var (print, macs) in macGroups)
        {
            if (macs.Count != 1 || !winGroups.TryGetValue(print, out var wins) || wins.Count != 1) continue;
            if (macs[0].Body.Instructions.Count < MinSeedInstructions) continue;
            PairMethod(macs[0], wins[0]);
        }

        // Grow: walk paired bodies; then settle repeated fingerprints inside already-paired types; repeat.
        for (var round = 0; round < 20; round++)
        {
            var before = _methods.Count + _types.Count + _fields.Count;
            while (_toWalk.TryDequeue(out var pair)) Walk(pair.Mac, pair.Win);
            foreach (var (print, macs) in macGroups)
            {
                if (!winGroups.TryGetValue(print, out var wins)) continue;
                foreach (var m in macs.Where(m => !_methods.ContainsKey(m)))
                {
                    if (!_types.TryGetValue(m.DeclaringType, out var wt)) continue;
                    var candidates = wins.Where(w => w.DeclaringType == wt && !_methodsBack.ContainsKey(w)).ToList();
                    var siblings = macs.Count(x => x.DeclaringType == m.DeclaringType && !_methods.ContainsKey(x));
                    if (candidates.Count == 1 && siblings == 1) PairMethod(m, candidates[0]);
                }
            }
            while (_toWalk.TryDequeue(out var pair)) Walk(pair.Mac, pair.Win);
            if (_methods.Count + _types.Count + _fields.Count == before) break;
        }

        // Enum values by constant; leftover fields of paired types by declaration order when the shapes agree.
        foreach (var (mt, wt) in _types.ToList())
        {
            if (mt.IsEnum)
            {
                foreach (var f in mt.Fields.Where(f => f.HasConstant))
                {
                    var match = wt.Fields.Where(w => w.HasConstant && Equals(w.Constant, f.Constant)).ToList();
                    if (match.Count == 1) PairField(f, match[0]);
                }
                continue;
            }
            var macFields = mt.Fields.ToList();
            var winFields = wt.Fields.ToList();
            if (macFields.Count == winFields.Count && macFields.Zip(winFields).All(p => SameShape(p.First.FieldType, p.Second.FieldType) && p.First.IsStatic == p.Second.IsStatic))
                foreach (var (f, w) in macFields.Zip(winFields)) PairField(f, w);
        }

        // Properties of paired types: by paired accessor, else by declaration order when the shapes agree.
        foreach (var (mt, wt) in _types.ToList())
        {
            var macProps = mt.Properties.ToList();
            var winProps = wt.Properties.ToList();
            if (macProps.Count != winProps.Count || !macProps.Zip(winProps).All(p => SameShape(p.First.PropertyType, p.Second.PropertyType))) continue;
            foreach (var (mp, wp) in macProps.Zip(winProps))
            {
                if (mp.GetMethod is { } mg && wp.GetMethod is { } wg && !_methods.ContainsKey(mg) && !_methodsBack.ContainsKey(wg)) PairMethod(mg, wg);
                if (mp.SetMethod is { } ms && wp.SetMethod is { } ws && !_methods.ContainsKey(ms) && !_methodsBack.ContainsKey(ws)) PairMethod(ms, ws);
            }
        }
        while (_toWalk.TryDequeue(out var late)) Walk(late.Mac, late.Win);

        var map = new NameMap();
        foreach (var (m, w) in _types) map.Types[m.FullName] = w.FullName;
        foreach (var (m, w) in _types) map.TypeTokens[w.MetadataToken.ToUInt32()] = m.FullName;
        foreach (var (m, w) in _methods)
        {
            map.Methods[w.MetadataToken.ToUInt32()] = m.MetadataToken.ToUInt32();
            map.Members[$"{m.DeclaringType.FullName}::{m.Name}/{m.Parameters.Count}"] = w.Name;
        }
        foreach (var (m, w) in _fields) map.Members[$"{m.DeclaringType.FullName}::{m.Name}"] = w.Name;
        foreach (var (mt, wt) in _types)
            foreach (var p in mt.Properties)
            {
                var accessor = p.GetMethod ?? p.SetMethod;
                if (accessor is null || !_methods.TryGetValue(accessor, out var wa)) continue;
                var wp = wt.Properties.FirstOrDefault(x => x.GetMethod == wa || x.SetMethod == wa);
                if (wp is not null) map.Members[$"{mt.FullName}::{p.Name}"] = wp.Name;
            }

        var real = macMethods.Count;
        Report = $"methods paired {_methods.Count} of {real} ({100.0 * _methods.Count / real:F1}%), types {_types.Count} of {_mac.GetTypes().Count()}, " +
                 $"fields {_fields.Count}, dropped as ambiguous {_conflicted.Count}; Windows methods left over (decoys or unmatched) {winMethods.Count - _methods.Count}";
        return map;
    }

    private static List<MethodDefinition> Bodies(ModuleDefinition module) =>
        module.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody && m.Body.Instructions.Count > 0).ToList();

    /// <summary>Beebyte-style names: 11 capital letters.</summary>
    public static bool LooksObfuscated(string name) => name.Length == 11 && name.All(c => c is >= 'A' and <= 'Z');

    // ---- pairing ----------------------------------------------------------------------------------------------------

    private void PairMethod(MethodDefinition mac, MethodDefinition win)
    {
        if (_conflicted.Contains(mac) || _conflicted.Contains(win)) return;
        if (_methods.TryGetValue(mac, out var had)) { if (had != win) Conflict(mac, had, win); return; }
        if (_methodsBack.TryGetValue(win, out var hadMac)) { if (hadMac != mac) Conflict(hadMac, win, mac); return; }
        if (mac.Body?.Instructions.Count is { } n && win.Body?.Instructions.Count != n) return; // shapes must agree to walk
        _methods[mac] = win;
        _methodsBack[win] = mac;
        PairType(mac.DeclaringType, win.DeclaringType);
        if (mac.HasBody && win.HasBody) _toWalk.Enqueue((mac, win));
    }

    private void Conflict(object a, object b, object c)
    {
        _conflicted.Add(a);
        _conflicted.Add(b);
        _conflicted.Add(c);
        if (a is MethodDefinition ma && _methods.Remove(ma, out var wa)) _methodsBack.Remove(wa);
        if (b is MethodDefinition mb && _methodsBack.Remove(mb, out var mbMac)) _methods.Remove(mbMac);
    }

    private void PairType(TypeDefinition mac, TypeDefinition win)
    {
        if (mac.Module != _mac || win.Module != _win || _conflicted.Contains(mac) || _conflicted.Contains(win)) return;
        if (_types.TryGetValue(mac, out var had)) { if (had != win) { _conflicted.Add(mac); _types.Remove(mac); _typesBack.Remove(had); } return; }
        if (_typesBack.TryGetValue(win, out var hadMac)) { if (hadMac != mac) { _conflicted.Add(win); _typesBack.Remove(win); _types.Remove(hadMac); } return; }
        _types[mac] = win;
        _typesBack[win] = mac;
        if (mac.DeclaringType is { } mo && win.DeclaringType is { } wo) PairType(mo, wo);
    }

    private void PairField(FieldDefinition mac, FieldDefinition win)
    {
        if (mac.Module != _mac || win.Module != _win || _conflicted.Contains(mac) || _conflicted.Contains(win)) return;
        if (_fields.TryGetValue(mac, out var had)) { if (had != win) { _conflicted.Add(mac); _fields.Remove(mac); _fieldsBack.Remove(had); } return; }
        if (_fieldsBack.ContainsKey(win)) return;
        _fields[mac] = win;
        _fieldsBack[win] = mac;
        PairType(mac.DeclaringType, win.DeclaringType);
    }

    /// <summary>Same instruction stream: everything referenced at the same place pairs up.</summary>
    private void Walk(MethodDefinition mac, MethodDefinition win)
    {
        var a = mac.Body.Instructions;
        var b = win.Body.Instructions;
        if (a.Count != b.Count) return;
        for (var i = 0; i < a.Count; i++)
        {
            if (a[i].OpCode != b[i].OpCode) return; // not the same body after all
            switch (a[i].Operand, b[i].Operand)
            {
                case (MethodReference ma, MethodReference mb):
                    PairRefs(ma, mb);
                    break;
                case (FieldReference fa, FieldReference fb):
                    if (Internal(fa.DeclaringType) && fa.Resolve() is { } fda && fb.Resolve() is { } fdb) PairField(fda, fdb);
                    PairTypeRefs(fa.DeclaringType, fb.DeclaringType);
                    PairTypeRefs(fa.FieldType, fb.FieldType);
                    break;
                case (TypeReference ta, TypeReference tb):
                    PairTypeRefs(ta, tb);
                    break;
            }
        }
        for (var i = 0; i < Math.Min(mac.Parameters.Count, win.Parameters.Count); i++) PairTypeRefs(mac.Parameters[i].ParameterType, win.Parameters[i].ParameterType);
        PairTypeRefs(mac.ReturnType, win.ReturnType);
        if (mac.Body.HasVariables && win.Body.HasVariables)
            for (var i = 0; i < Math.Min(mac.Body.Variables.Count, win.Body.Variables.Count); i++)
                PairTypeRefs(mac.Body.Variables[i].VariableType, win.Body.Variables[i].VariableType);
    }

    private void PairRefs(MethodReference a, MethodReference b)
    {
        PairTypeRefs(a.DeclaringType, b.DeclaringType);
        if (a is GenericInstanceMethod ga && b is GenericInstanceMethod gb)
            for (var i = 0; i < Math.Min(ga.GenericArguments.Count, gb.GenericArguments.Count); i++) PairTypeRefs(ga.GenericArguments[i], gb.GenericArguments[i]);
        if (!Internal(a.DeclaringType)) return;
        var da = a.Resolve();
        var db = b.Resolve();
        if (da is not null && db is not null && da.Parameters.Count == db.Parameters.Count) PairMethod(da, db);
    }

    private void PairTypeRefs(TypeReference a, TypeReference b)
    {
        switch (a, b)
        {
            case (GenericInstanceType ga, GenericInstanceType gb):
                PairTypeRefs(ga.ElementType, gb.ElementType);
                for (var i = 0; i < Math.Min(ga.GenericArguments.Count, gb.GenericArguments.Count); i++) PairTypeRefs(ga.GenericArguments[i], gb.GenericArguments[i]);
                return;
            case (TypeSpecification sa, TypeSpecification sb):
                PairTypeRefs(sa.ElementType, sb.ElementType);
                return;
        }
        if (a is GenericParameter || b is GenericParameter || !Internal(a)) return;
        if (a.Resolve() is { } da && b.Resolve() is { } db) PairType(da, db);
    }

    private bool Internal(TypeReference t)
    {
        while (t is TypeSpecification spec) t = spec.ElementType;
        return t is not GenericParameter && (t is TypeDefinition || t.Scope is ModuleDefinition || t.Scope == _mac || t.Scope == _win);
    }

    private bool SameShape(TypeReference a, TypeReference b) => Internal(a) ? Internal(b) : a.FullName == b.FullName;

    // ---- fingerprints -----------------------------------------------------------------------------------------------

    private string Fingerprint(MethodDefinition m)
    {
        var sb = new StringBuilder();
        sb.Append(m.IsStatic ? 'S' : 'I').Append(m.Parameters.Count).Append(':').Append(Norm(m.ReturnType)).Append('(');
        foreach (var p in m.Parameters) sb.Append(Norm(p.ParameterType)).Append(',');
        sb.Append(")|");
        var ins = m.Body.Instructions;
        var index = new Dictionary<Instruction, int>(ins.Count);
        for (var i = 0; i < ins.Count; i++) index[ins[i]] = i;
        for (var i = 0; i < ins.Count; i++)
        {
            sb.Append(ins[i].OpCode.Code).Append(' ');
            sb.Append(ins[i].Operand switch
            {
                null => "",
                Instruction target => "@" + (index.GetValueOrDefault(target) - i),
                Instruction[] targets => string.Join(",", targets.Select(t => "@" + (index.GetValueOrDefault(t) - i))),
                string s => "\"" + s + "\"",
                VariableDefinition v => "v" + v.Index,
                ParameterDefinition p => "a" + p.Index,
                MethodReference mr => NormMethod(mr),
                FieldReference fr => Internal(fr.DeclaringType) ? "?F" + Norm(fr.FieldType) : fr.DeclaringType.FullName + "::" + fr.Name,
                TypeReference tr => Norm(tr),
                CallSite => "callsite",
                var other => Convert.ToString(other, System.Globalization.CultureInfo.InvariantCulture),
            });
            sb.Append(';');
        }
        return sb.ToString();
    }

    private string NormMethod(MethodReference m)
    {
        var args = m is GenericInstanceMethod g ? "<" + string.Join(",", g.GenericArguments.Select(Norm)) + ">" : "";
        if (Internal(m.DeclaringType)) return $"?M{m.Parameters.Count}{(m.HasThis ? "i" : "s")}{args}:{Norm(m.ReturnType)}";
        return $"{Norm(m.DeclaringType)}::{m.Name}{args}({string.Join(",", m.Parameters.Select(p => Norm(p.ParameterType)))})";
    }

    private string Norm(TypeReference t) => t switch
    {
        GenericInstanceType g => Norm(g.ElementType) + "<" + string.Join(",", g.GenericArguments.Select(Norm)) + ">",
        ArrayType a => Norm(a.ElementType) + "[]",
        ByReferenceType r => Norm(r.ElementType) + "&",
        PointerType p => Norm(p.ElementType) + "*",
        GenericParameter gp => (gp.Type == GenericParameterType.Method ? "!!" : "!") + gp.Position,
        _ when Internal(t) => t.Resolve()?.IsEnum == true ? "?E" : t.IsValueType ? "?V" : "?T",
        _ => t.FullName,
    };
}
