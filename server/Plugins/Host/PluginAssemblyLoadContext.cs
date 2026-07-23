using System.Reflection;
using System.Runtime.Loader;

namespace StandChillow.LanServer.Plugins;

/// <summary>
/// Collectible load context so <c>plugins reload</c> can unload assemblies (best-effort).
/// </summary>
internal sealed class PluginAssemblyLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public PluginAssemblyLoadContext(string pluginPath)
        : base(isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(pluginPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Prefer shared API already loaded in the default context.
        if (assemblyName.Name is "StandChillow.Server.Plugins")
            return null;

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is not null ? LoadFromAssemblyPath(path) : null;
    }
}
