using System.Runtime.CompilerServices;
using CH.Server;

namespace CH.Tests;

/// <summary>Runs before any test: test servers use the built-in class kits, so editing class-kits.json can't break them.</summary>
internal static class TestSetup
{
    [ModuleInitializer]
    internal static void Init() => ServerOptions.DefaultClassKitsPath = null;
}
