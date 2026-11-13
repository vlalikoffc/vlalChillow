using System.Net;
using LiteNetLib;
using StandChillow.LanServer.Bans;
using StandChillow.LanServer.Config;
using StandChillow.LanServer.Dashboard;
using StandChillow.LanServer.Net.Lobby;

namespace StandChillow.LanServer.Net;

/// <summary>
/// Авторизация админского ника кодом на текущий запуск.
/// </summary>
/// <remarks>
/// <para>
/// Ник присылает клиент — значит «влал» может набрать кто угодно. Пока IP не предъявил код,
/// такой игрок не попадает в лобби (видит только Server, как забаненный), не может зайти в
/// матч и не может выполнить ни одной команды. Код печатается в консоль сервера при старте,
/// вводится обычным сообщением в чат, и после этого адрес авторизован до перезапуска.
/// </para>
/// <para>
/// Это не замена банам: баны работают по нику+IP и от авторизации не зависят.
/// </para>
/// </remarks>
public sealed partial class GameNetHost
{
    /// <summary>Сколько неверных кодов терпим, прежде чем отключить сокет.</summary>
    private const int AdminAuthMaxAttempts = 5;

    private readonly AdminAuth _adminAuth = new();
    private readonly object _pendingAdminGate = new();
    private readonly Dictionary<NetPeer, PendingAdmin> _pendingAdmins = new();

    private sealed class PendingAdmin
    {
        public string Nick = string.Empty;
        public int Attempts;
    }

    /// <summary>Код этого запуска (для консоли и лога).</summary>
    internal AdminAuth Auth => _adminAuth;

    /// <summary>
    /// Ник админский И адрес уже авторизован — только тогда у него есть права.
    /// </summary>
    internal bool IsAdminAuthorized(string? nick, IPAddress? ip) =>
        _config.IsAdmin(nick) && _adminAuth.IsAuthorized(ip);

    /// <summary>
    /// Админский ник, но адрес код ещё не предъявлял: в игру пускать нельзя.
    /// </summary>
    internal bool IsUnauthorizedAdmin(string? nick, IPAddress? ip) =>
        _config.IsAdmin(nick) && !_adminAuth.IsAuthorized(ip);

    /// <summary>Код на этот запуск — в консоль и в панель (там его видит только хост).</summary>
    /// <remarks>
    /// Код печатается всегда, даже когда админ-списка ещё нет: его могут добавить в конфиг
    /// на ходу (сервер перечитывает файл), и тогда код уже будет под рукой.
    /// </remarks>
    private void LogAdminAuthStartup()
    {
        Console.WriteLine(
            $"[auth] код админа на этот запуск: {_adminAuth.Code} " +
            "(вводится в чат; действует до перезапуска, IP запоминается)");
        if (_config.Admin.Nicknames.Count == 0)
        {
            Console.WriteLine(
                "[auth] админских ников нет — код никому не понадобится, пока в " +
                "config.json admin.nicknames пусто");
            return;
        }

        Console.WriteLine(
            $"[auth] админские ники: {string.Join(", ", _config.Admin.Nicknames)} — " +
            "без кода они видят только Server, как забаненные");
    }

    /// <summary>
    /// Вход под админским ником без кода: теневой лобби (роспись из одного Server) и просьба
    /// ввести код. Никаких команд, никакого матча.
    /// </summary>
    private void HandleAdminAuthJoin(NetPeer peer, JoinLobbyRequest req)
    {
        var result = _session.HandleShadowJoinRequest(peer, req);
        if (result.ResponseBytes is not null)
            Send(peer, result.ResponseBytes);

        if (!result.Success)
        {
            Console.WriteLine(
                $"[auth] теневой вход отклонён: {result.FailReason} ({result.Detail})");
            return;
        }

        var nick = ServerConfig.NormalizeNick(req.Profile.Name);
        lock (_pendingAdminGate)
        {
            _pendingAdmins[peer] = new PendingAdmin { Nick = nick };
        }

        Console.WriteLine(
            $"[auth] '{nick}' {peer.Address} — админский ник без кода: " +
            "лобби не выдан, команды закрыты, ждём код в чате");

        DashboardHub.PostChat(
            LobbySession.ServerPlayerName,
            $"[auth] {nick} ({peer.Address}) — админский ник без кода, ждём авторизацию",
            fromServer: true);

        SendServerChatTo(peer, $"ник '{nick}' админский — нужна авторизация");
        SendServerChatTo(peer, $"код на этот запуск: {_adminAuth.Code.Length} символов, " +
                               "он напечатан в консоли сервера");
        SendServerChatTo(peer, "напиши код сюда, в чат (регистр не важен)");
    }

    private bool IsPendingAdmin(NetPeer peer)
    {
        lock (_pendingAdminGate)
            return _pendingAdmins.ContainsKey(peer);
    }

    /// <summary>
    /// Сообщение от неавторизованного админа: принимаем только код. Возвращает <c>true</c>,
    /// если сообщение обработано здесь (дальше его никто не видит).
    /// </summary>
    private bool HandlePendingAdminChat(NetPeer peer, string message)
    {
        PendingAdmin? pending;
        lock (_pendingAdminGate)
        {
            if (!_pendingAdmins.TryGetValue(peer, out pending))
                return false;
        }

        if (_adminAuth.Matches(message))
        {
            _adminAuth.Authorize(peer.Address);
            lock (_pendingAdminGate)
                _pendingAdmins.Remove(peer);

            Console.WriteLine(
                $"[auth] код принят: '{pending.Nick}' {peer.Address} — адрес авторизован " +
                $"до перезапуска (всего адресов: {_adminAuth.AuthorizedCount})");
            DashboardHub.PostChat(
                LobbySession.ServerPlayerName,
                $"[auth] {pending.Nick} ({peer.Address}) авторизован",
                fromServer: true);

            SendServerChatTo(peer, "код принят — ты авторизован до перезапуска сервера");
            SendServerChatTo(peer, "зайди в лобби заново");
            Schedule(400, () =>
            {
                try { peer.Disconnect(); }
                catch { /* уже мёртв */ }
            });
            return true;
        }

        pending.Attempts++;
        Console.WriteLine(
            $"[auth] неверный код от '{pending.Nick}' {peer.Address} " +
            $"(попытка {pending.Attempts}/{AdminAuthMaxAttempts})");

        if (pending.Attempts >= AdminAuthMaxAttempts)
        {
            lock (_pendingAdminGate)
                _pendingAdmins.Remove(peer);
            SendServerChatTo(peer, "слишком много неверных кодов — соединение закрыто");
            Schedule(300, () =>
            {
                try { peer.Disconnect(); }
                catch { /* уже мёртв */ }
            });
            return true;
        }

        SendServerChatTo(peer, $"неверный код (попытка {pending.Attempts} из " +
                               $"{AdminAuthMaxAttempts}), попробуй ещё");
        return true;
    }

    private void ForgetPendingAdmin(NetPeer peer)
    {
        lock (_pendingAdminGate)
            _pendingAdmins.Remove(peer);
    }

    private int PendingAdminCount
    {
        get { lock (_pendingAdminGate) return _pendingAdmins.Count; }
    }

    /// <summary>
    /// <c>/whoami</c>: что сервер реально знает о написавшем — ник после нормализации, id,
    /// адрес и статус админа. Отвечает на «почему я не админ».
    /// </summary>
    private void HandleWhoAmICommand(Action<string> reply, ConnectedPlayer? player)
    {
        if (player is null)
        {
            reply("whoami — только из чата игрока");
            return;
        }

        var nick = ServerConfig.NormalizeNick(player.Name);
        var id = MatchPublicId.ForNick(nick);
        var isAdminNick = _config.IsAdmin(nick);
        var authorized = _adminAuth.IsAuthorized(player.Peer.Address);

        reply($"ник: '{nick}' · id: #{id} · IP: {player.Peer.Address}");
        reply(isAdminNick
            ? (authorized ? "админ: да, код принят" : "админ: ник админский, код не введён")
            : "админ: нет");
        reply(_config.Admin.Nicknames.Count == 0
            ? "в config.json admin.nicknames пусто — админ-команд ни у кого нет"
            : $"админы в конфиге: {string.Join(", ", _config.Admin.Nicknames)}");
    }

    /// <summary>
    /// Консольная команда <c>auth</c>: показать код, состояние и сбросить.
    /// </summary>
    /// <remarks>
    /// Только для консоли/панели: из чата код показывать нельзя, иначе он перестанет быть
    /// секретом.
    /// </remarks>
    private void HandleAuthCommand(Action<string> reply, string args, ConnectedPlayer? player)
    {
        if (player is not null)
        {
            reply("команда auth — только из консоли сервера");
            return;
        }

        var action = (args ?? string.Empty).Trim().ToLowerInvariant();
        if (action is "reset" or "new" or "сброс")
        {
            var code = _adminAuth.Reset();
            Console.WriteLine($"[auth] код сброшен, новый: {code}");
            reply($"новый код: {code}");
            reply("все авторизованные адреса забыты");
            return;
        }

        reply($"код на этот запуск: {_adminAuth.Code}");
        reply($"авторизованных адресов: {_adminAuth.AuthorizedCount}; " +
              $"ждут код: {PendingAdminCount}");
        foreach (var ip in _adminAuth.AuthorizedIps)
            reply($"  авторизован: {ip}");
        reply("auth reset — новый код и сброс авторизаций");
    }
}
