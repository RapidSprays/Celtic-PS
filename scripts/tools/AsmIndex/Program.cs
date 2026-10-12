// AsmIndex: reproducible, per-build index of a client assembly.
//
//   dotnet run --project scripts/tools/AsmIndex -- <assembly.dll> <outDir>
//
// Writes (outDir is outside git, e.g. ~/CelticHeroesAnalysis/indexes/<build>/):
//   enums.json     every enum: name, underlying type, members (diff these across builds to track opcode changes)
//   refs.csv       method,category,referenced member   for networking/crypto/anti-cheat/service APIs
//   endpoints.csv  method,literal                      for string literals that look like URLs or host names
using System.Text.Json;
using System.Text.RegularExpressions;
using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: AsmIndex <assembly.dll> <outDir>");
    return 2;
}

var asmPath = Path.GetFullPath(args[0]);
var outDir = Path.GetFullPath(args[1]);
Directory.CreateDirectory(outDir);

var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(Path.GetDirectoryName(asmPath)!);
var module = ModuleDefinition.ReadModule(asmPath, new ReaderParameters { AssemblyResolver = resolver, ReadSymbols = false });

// Referenced-namespace prefix → category
var categories = new (string Prefix, string Category)[]
{
    ("Lidgren.Network", "lidgren"),
    ("System.Net.Sockets", "sockets"),
    ("System.Net.Http", "http"),
    ("System.Net.WebClient", "http"),
    ("System.Net.HttpWebRequest", "http"),
    ("UnityEngine.Networking.UnityWebRequest", "http"),
    ("Google.Protobuf", "protobuf"),
    ("Org.BouncyCastle", "bouncycastle"),
    ("System.Security.Cryptography", "crypto"),
    ("K4os.Compression.LZ4", "lz4"),
    ("ZstdNet", "zstd"),
    ("CodeStage.AntiCheat", "actk"),
    ("Firebase", "firebase"),
    ("Steamworks", "steamworks"),
    ("UnityEngine.Purchasing", "iap"),
    ("Facebook.Unity", "facebook"),
    ("OneSignalSDK", "onesignal"),
    ("Fyber", "ads"),
};

static string FullName(MemberReference m) => m.DeclaringType != null ? $"{m.DeclaringType.FullName}::{m.Name}" : m.FullName;

var hostLike = new Regex(@"^(https?|wss?|ftp)://|^[a-z0-9-]+(\.[a-z0-9-]+)*\.(com|net|org|io|co|uk|gg|cloud|dev)(:\d+)?(/.*)?$", RegexOptions.IgnoreCase);

var enums = new List<EnumInfo>();
var refs = new SortedSet<string>(StringComparer.Ordinal);
var endpoints = new SortedSet<string>(StringComparer.Ordinal);

static string Csv(string s) => s.Contains(',') || s.Contains('"') ? $"\"{s.Replace("\"", "\"\"")}\"" : s;

foreach (var type in module.GetTypes())
{
    if (type.IsEnum)
    {
        var underlying = type.Fields.First(f => f.Name == "value__").FieldType.FullName;
        var members = type.Fields.Where(f => f.IsStatic && f.HasConstant)
            .Select(f => new EnumMember(f.Name, Convert.ToInt64(f.Constant))).ToList();
        enums.Add(new EnumInfo(type.FullName, underlying, members.Count, members));
        continue;
    }

    foreach (var method in type.Methods.Where(m => m.HasBody))
    {
        var caller = $"{type.FullName}::{method.Name}";
        foreach (var ins in method.Body.Instructions)
        {
            if (ins.Operand is MemberReference mr && ins.OpCode.OperandType is OperandType.InlineMethod or OperandType.InlineField or OperandType.InlineTok or OperandType.InlineType)
            {
                var target = mr is TypeReference tr ? tr.FullName : FullName(mr);
                foreach (var (prefix, category) in categories)
                {
                    if (target.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        refs.Add($"{Csv(caller)},{category},{Csv(target)}");
                        break;
                    }
                }
            }
            else if (ins.OpCode == OpCodes.Ldstr && ins.Operand is string s && s.Length < 300 && hostLike.IsMatch(s))
            {
                endpoints.Add($"{Csv(caller)},{Csv(s)}");
            }
        }
    }
}

var json = new JsonSerializerOptions { WriteIndented = true };
File.WriteAllText(Path.Combine(outDir, "enums.json"), JsonSerializer.Serialize(new
{
    assembly = module.Assembly.FullName,
    mvid = module.Mvid,
    enums = enums.OrderBy(e => e.name, StringComparer.Ordinal),
}, json));
File.WriteAllLines(Path.Combine(outDir, "refs.csv"), new[] { "method,category,target" }.Concat(refs));
File.WriteAllLines(Path.Combine(outDir, "endpoints.csv"), new[] { "method,literal" }.Concat(endpoints));

Console.WriteLine($"{module.Assembly.Name.Name}: {enums.Count} enums, {refs.Count} API refs, {endpoints.Count} endpoint literals → {outDir}");
return 0;

record EnumMember(string name, long value);
record EnumInfo(string name, string underlying, int count, List<EnumMember> members);
