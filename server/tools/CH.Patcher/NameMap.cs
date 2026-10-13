using System.Text.Json;
using Mono.Cecil;

namespace CH.Patcher;

/// <summary>
/// Readable (Mac build) names → the Windows build's obfuscated names (docs/windows-names.md). Built on the host from
/// both builds by <see cref="NameMatcher"/> (<c>CH.Patcher map</c>) and embedded in the patcher per Windows build; the
/// Mac build uses <see cref="Identity"/>. The patcher works on Mac names throughout and asks this map where things are.
/// Holds names and metadata tokens only, nothing of the game's code.
/// </summary>
public sealed class NameMap
{
    public static readonly NameMap Identity = new() { IsIdentity = true };

    public bool IsIdentity { get; private init; }

    /// <summary>Mac full type name → Windows full type name (nested types as Outer/Inner).</summary>
    public Dictionary<string, string> Types { get; init; } = new();

    /// <summary>"MacType::macMember" (methods as "::name/paramCount") → Windows member name.</summary>
    public Dictionary<string, string> Members { get; init; } = new();

    /// <summary>Windows method token → Mac method token: the real methods (decoys never appear here).</summary>
    public Dictionary<uint, uint> Methods { get; init; } = new();

    /// <summary>Windows type token → Mac full type name.</summary>
    public Dictionary<uint, string> TypeTokens { get; init; } = new();

    // ---- views for the patcher (identity on the Mac build) --------------------------------------------------------

    private Dictionary<string, string>? _macByWinType;

    /// <summary>The Mac name of a type in the module being patched.</summary>
    public string MacName(TypeReference type)
    {
        if (IsIdentity) return type.FullName;
        if (type.Resolve() is { } def && TypeTokens.TryGetValue(def.MetadataToken.ToUInt32(), out var mac)) return mac;
        _macByWinType ??= Types.GroupBy(kv => kv.Value).ToDictionary(g => g.Key, g => g.First().Key);
        return _macByWinType.GetValueOrDefault(type.FullName, type.FullName);
    }

    private Dictionary<string, string>? _macByWinMember;

    /// <summary>The Mac name of a field, method or property in the module being patched.</summary>
    public string MacName(MemberReference member)
    {
        if (IsIdentity) return member.Name;
        var macType = MacName(member.DeclaringType);
        _macByWinMember ??= Members.GroupBy(kv => kv.Key[..kv.Key.LastIndexOf("::", StringComparison.Ordinal)] + "::" + kv.Value)
            .ToDictionary(g => g.Key, g => g.First().Key);
        var arity = member is MethodReference m ? $"/{m.Parameters.Count}" : "";
        if (_macByWinMember.TryGetValue($"{macType}::{member.Name}{arity}", out var key)) return MemberPart(key);
        if (arity.Length > 0 && _macByWinMember.TryGetValue($"{macType}::{member.Name}", out key)) return MemberPart(key);
        return member.Name;
    }

    private static string MemberPart(string key)
    {
        var name = key[(key.LastIndexOf("::", StringComparison.Ordinal) + 2)..];
        var slash = name.IndexOf('/');
        return slash < 0 ? name : name[..slash];
    }

    /// <summary>Real game code (not an obfuscator decoy). Everything is real on the Mac build.</summary>
    public bool IsReal(MethodDefinition method) => IsIdentity || Methods.ContainsKey(method.MetadataToken.ToUInt32());

    public TypeDefinition? FindType(ModuleDefinition module, string macName)
    {
        var name = IsIdentity ? macName : Types.GetValueOrDefault(macName, macName);
        return module.GetType(name);
    }

    // ---- persistence ----------------------------------------------------------------------------------------------

    private sealed record Stored(Dictionary<string, string> Types, Dictionary<string, string> Members, Dictionary<string, uint> Methods,
        Dictionary<string, string> TypeTokens);

    public string ToJson() => JsonSerializer.Serialize(new Stored(Types, Members,
        Methods.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value), TypeTokens.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value)));

    public static NameMap FromJson(string json)
    {
        var s = JsonSerializer.Deserialize<Stored>(json) ?? throw new InvalidDataException("empty name map");
        return new NameMap
        {
            Types = s.Types, Members = s.Members,
            Methods = s.Methods.ToDictionary(kv => uint.Parse(kv.Key), kv => kv.Value),
            TypeTokens = s.TypeTokens.ToDictionary(kv => uint.Parse(kv.Key), kv => kv.Value),
        };
    }

    /// <summary>The embedded map for a Windows Assembly-CSharp.dll hash, if the kit carries one.</summary>
    public static NameMap? Embedded(string dllSha256)
    {
        using var stream = typeof(NameMap).Assembly.GetManifestResourceStream($"names-{dllSha256}.json");
        return stream is null ? null : FromJson(new StreamReader(stream).ReadToEnd());
    }

    /// <summary>
    /// Lines for the client mod (CHPrivateMods.names.txt): "T|macType|winType" and "M|macType::member[/arity]|winName".
    /// </summary>
    public IEnumerable<string> ModLines() =>
        Types.Where(kv => kv.Key != kv.Value).Select(kv => $"T|{kv.Key}|{kv.Value}")
            .Concat(Members.Where(kv => MemberPart(kv.Key) != kv.Value).Select(kv => $"M|{kv.Key}|{kv.Value}"));
}
