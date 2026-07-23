namespace StandChillow.Server.Plugins;

public interface IPluginLog
{
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? ex = null);
    void Debug(string message);
}
