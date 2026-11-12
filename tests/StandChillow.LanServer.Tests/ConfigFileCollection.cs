using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Тесты, которые реально читают <c>config.json</c> с диска. <see cref="Config.ServerConfig.Load"/>
/// пишет в глобальные <c>LastLoadedPath</c>/<c>Current</c>, поэтому такие тесты нельзя
/// выполнять параллельно с теми, кто эти статики читает.
/// </summary>
[CollectionDefinition("config-file", DisableParallelization = true)]
public sealed class ConfigFileCollection
{
}
