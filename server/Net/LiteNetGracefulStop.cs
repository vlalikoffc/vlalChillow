using LiteNetLib;

namespace StandChillow.LanServer.Net;

/// <summary>
/// Flush LiteNetLib disconnect packets before <see cref="NetManager.Stop"/> so phones
/// see an immediate disconnect instead of timing out after an abrupt process kill.
/// </summary>
internal static class LiteNetGracefulStop
{
    public static void DisconnectFlushAndStop(NetManager manager, int polls = 10, int sleepMs = 20)
    {
        try
        {
            if (manager.IsRunning)
                manager.DisconnectAll();
        }
        catch
        {
            /* ignore */
        }

        for (var i = 0; i < polls; i++)
        {
            try { manager.PollEvents(); }
            catch { /* ignore */ }
            Thread.Sleep(sleepMs);
        }

        try { manager.Stop(); }
        catch { /* ignore */ }
    }
}
