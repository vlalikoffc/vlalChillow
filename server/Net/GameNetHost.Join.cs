using StandChillow.LanServer.Net.Lobby;
using StandChillow.LanServer.Net.Match.Kernel;

namespace StandChillow.LanServer.Net;

public sealed partial class GameNetHost
{
    /// <summary>
    /// Откуда игроку показать адрес сайта, если комнаты нет. Заполняется при запуске
    /// (<c>web.publicBaseUrl</c> → внешний IP → LAN). Пусто — адрес не показываем.
    /// </summary>
    public SiteAddressResolver? SiteAddress { get; set; }

    /// <summary>
    /// <c>/join &lt;режим&gt; #&lt;номер&gt;</c> — войти именно в эту комнату.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Только из лобби: в матче команда не имеет смысла — игрок уже в комнате, а
    /// переезжать между комнатами провод не умеет. Попытку из матча честно отклоняем.
    /// </para>
    /// <para>
    /// Успех — не «переключение» комнаты, а бронь: клиент, зайдя в игру, пришлёт
    /// <c>JoinRoom(nick, 'Dedik')</c>, и разбор пакета найдёт бронь и пустит в нужную
    /// комнату (см. <c>PendingRoomAssignments</c>). Иначе игроку пришлось бы диктовать
    /// друзьям пароль, которого на проводе нет.
    /// </para>
    /// </remarks>
    private void HandleJoinCommand(
        Action<string> reply,
        string args,
        string who,
        ConnectedPlayer? player)
    {
        // Комнаты создаются только с сайта; команда — только вход.
        var parsed = JoinCommandParser.Parse(args);
        if (!parsed.Ok)
        {
            reply("/join <режим> #<номер>   например: /join 2v2 #3");
            reply("комнаты создаются на сайте — кнопка «создать»");
            return;
        }

        if (!GameModeCatalog.TryResolveMode(parsed.ModeToken, out var mode))
        {
            reply($"неизвестный режим '{parsed.ModeToken}'");
            foreach (var line in GameModeCatalog.FormatModeHelp().Split('\n'))
                reply(line.TrimEnd());
            return;
        }

        var rooms = _match.Rooms;
        if (rooms is null || !rooms.TryFind(mode.GameModeId, parsed.RoomNumber, out var room))
        {
            var display = mode.DisplayName;
            var address = SiteAddress?.Resolve() ?? "";
            var msg = address.Length == 0
                ? $"Комната «{display} * {parsed.RoomNumber}» не найдена."
                : JoinCommandParser.FormatNotFound(display, parsed.RoomNumber, address);
            reply(msg);
            Console.WriteLine(
                $"[lobby] {who}: {msg} (комнат панели: {rooms?.Count ?? 0})");
            return;
        }

        if (player is null)
        {
            // Из консоли/дашборда ник неизвестен — бронь не к кому привязать.
            reply($"комната «{room.Name}» есть; /join работает из игры (нужен ник)");
            return;
        }

        var nick = player.Name;
        // IdentityKey появится только на 7777 (handshake 207), в лобби его ещё нет —
        // бронируем по нику + адресу, этого достаточно для одного игрока в комнате.
        if (!_match.PendingRooms.Reserve(
                room.PasswordKey,
                nick,
                identityKey: null,
                address: player.Peer.Address?.ToString()))
        {
            reply("не удалось забронировать комнату — переполнен список ожидания, попробуй ещё");
            Console.WriteLine(
                $"[lobby] {who}: бронь '{room.PasswordKey}' для '{nick}' отклонена (capacity)");
            return;
        }

        // Комнату создали с панели — она уже armed; здесь ничего не армим.

        // Бронь лежит — зовём именно этого игрока на 7777. Клиент не угадывает:
        // адрес матча приходит ему одному в op9, остальные остаются в лобби.
        var sent = NotifyPanelRoomReady(nick);

        var ok =
            $"комната «{room.Name}» ({mode.DisplayName} * {parsed.RoomNumber}) — " +
            (sent ? "входи" : "бронь есть, зайди в игру и ты попадёшь туда (2 мин)");
        Console.WriteLine($"[lobby] {who}: {ok}");
        reply(ok);
        BroadcastServerChat($"{nick} → комната «{room.Name}» {mode.DisplayName} * {parsed.RoomNumber}");
    }
}
