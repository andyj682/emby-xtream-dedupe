// Loads a built plugin DLL against one Emby SDK and forces every type and method to resolve.
//
// Usage: dotnet run --project scripts/sdk-load-check -- <plugin.dll> <sdk-dir>
//
// One plugin DLL is shipped for both Emby 4.9 and 4.10 (ADR-017). It is compiled against the 4.9
// SDK, so the compiler never sees the 4.10 interfaces. This check is what catches a 4.10 SDK
// change the DLL does not satisfy: a missing interface member fails type loading ("does not have
// an implementation"), and a removed or changed member fails when the calling method is compiled
// (MissingMethodException). Exits 1 on any failure.
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: sdk-load-check <plugin.dll> <sdk-dir>");
    return 2;
}

var pluginPath = Path.GetFullPath(args[0]);
var sdkDir = Path.GetFullPath(args[1]);

// Emby installs the plugin as a single DLL and supplies everything else itself, so only
// MediaBrowser.* is resolved, from the SDK directory under test. System.Text.Json and the other
// libraries come from this runtime's framework, the same way the Emby host provides its own.
var context = new AssemblyLoadContext("sdk-load-check");
context.Resolving += (ctx, name) =>
{
    var candidate = Path.Combine(sdkDir, name.Name + ".dll");
    return File.Exists(candidate) ? ctx.LoadFromAssemblyPath(candidate) : null;
};

var failures = new SortedSet<string>();
var assembly = context.LoadFromAssemblyPath(pluginPath);

Type[] types;
try
{
    types = assembly.GetTypes();
}
catch (ReflectionTypeLoadException ex)
{
    foreach (var loaderException in ex.LoaderExceptions.Where(e => e != null))
        failures.Add(loaderException.GetType().Name + ": " + loaderException.Message);
    types = ex.Types.Where(t => t != null).ToArray();
}

const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
    | BindingFlags.Static | BindingFlags.DeclaredOnly;
var compiled = 0;

foreach (var type in types)
{
    if (type.ContainsGenericParameters) continue;

    IEnumerable<MethodBase> methods;
    try
    {
        methods = type.GetMethods(All).Cast<MethodBase>().Concat(type.GetConstructors(All)).ToArray();
    }
    catch (Exception ex)
    {
        failures.Add(type.FullName + ": " + ex.GetType().Name + ": " + ex.Message);
        continue;
    }

    foreach (var method in methods)
    {
        if (method.IsAbstract || method.ContainsGenericParameters) continue;
        try
        {
            if (method.GetMethodBody() == null) continue;
            RuntimeHelpers.PrepareMethod(method.MethodHandle);
            compiled++;
        }
        catch (Exception ex)
        {
            failures.Add(ex.GetType().Name + ": " + ex.Message);
        }
    }
}

Console.WriteLine($"{Path.GetFileName(pluginPath)} against {sdkDir}: {types.Length} types, {compiled} methods compiled, {failures.Count} failures");
foreach (var failure in failures) Console.WriteLine("  " + failure);
return failures.Count == 0 ? 0 : 1;
