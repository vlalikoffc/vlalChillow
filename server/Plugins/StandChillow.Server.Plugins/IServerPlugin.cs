namespace StandChillow.Server.Plugins;

/// <summary>
/// Entry point for a LanServer plugin assembly. Implement in a class library that references
/// <c>StandChillow.Server.Plugins</c> only (not the host exe). Drop the built DLL into
/// the host <c>plugins/</c> folder.
/// </summary>
public interface IServerPlugin
{
    /// <summary>Stable id used for data dir <c>plugins/{Name}/</c> and logs.</summary>
    string Name { get; }

    /// <summary>Optional display version (informational).</summary>
    string? Version => null;

    void OnLoad(IPluginContext context);

    void OnUnload();
}
