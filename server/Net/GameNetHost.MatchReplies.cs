using System.Net;
using StandChillow.LanServer.Config;

namespace StandChillow.LanServer.Net;

/// <summary>
/// Отложенные ответы в матч-чат: команда пришла раньше, чем игрок успел подключиться к 7777.
/// </summary>
/// <remarks>
/// <para>
/// Разбор лога: <c>/start</c> из лобби отвечает мгновенно, но матч-сокет в этот момент ещё не
/// принял ни одного пира — ответить в катку физически некому. Клиент лобби уже закрыл, поэтому
/// игрок видит тишину и делает вывод «в катке команды не работают».
/// </para>
/// <para>
/// Лечим не новым пакетом, а тем же <c>ChatManager</c>: ответ запоминается и досылается, когда
/// пир с таким ником действительно есть в матче. Очередь живёт в памяти, ограничена по размеру и
/// по времени (минута).
/// </para>
/// </remarks>
public sealed partial class GameNetHost
{
    private const int PendingReplyTtlSeconds = 60;
    private const int PendingReplyMaxPerNick = 8;

    private readonly object _replyGate = new();

    /// <summary>Ник (нормализованный) → ещё не доставленные ответы.</summary>
    private readonly Dictionary<string, List<PendingMatchReply>> _matchReplies = new();

    private sealed class PendingMatchReply
    {
        public required string Text { get; init; }
        public IPAddress? Address { get; init; }
        public DateTime CreatedAt { get; init; }

        public bool IsExpired =>
            DateTime.UtcNow - CreatedAt > TimeSpan.FromSeconds(PendingReplyTtlSeconds);
    }

    /// <summary>
    /// Ответ не нашёл адресата в матче: придержим его и попробуем ещё несколько раз — пир
    /// подключается через несколько секунд после op9.
    /// </summary>
    private void RememberMatchReply(string nick, string text, IPAddress? address)
    {
        var key = ServerConfig.NormalizeNick(nick);
        if (key.Length == 0 || string.IsNullOrWhiteSpace(text))
            return;

        lock (_replyGate)
        {
            if (!_matchReplies.TryGetValue(key, out var list))
                _matchReplies[key] = list = new List<PendingMatchReply>();
            list.Add(new PendingMatchReply
            {
                Text = text,
                Address = address,
                CreatedAt = DateTime.UtcNow,
            });
            if (list.Count > PendingReplyMaxPerNick)
                list.RemoveAt(0);
        }

        Console.WriteLine($"[chat] Server → {nick}: ответ отложен до подключения к катке");
        Schedule(1200, () => FlushMatchReplies(nick));
        Schedule(3000, () => FlushMatchReplies(nick));
        Schedule(6000, () => FlushMatchReplies(nick));
    }

    /// <summary>
    /// Дослать накопленное, если адресат уже в матче. Просроченное выбрасываем молча — лучше
    /// потерять старый ответ, чем показать его через десять минут не к месту.
    /// </summary>
    private void FlushMatchReplies(string nick)
    {
        var key = ServerConfig.NormalizeNick(nick);
        if (key.Length == 0)
            return;

        List<PendingMatchReply>? batch;
        lock (_replyGate)
        {
            if (!_matchReplies.TryGetValue(key, out var list) || list.Count == 0)
                return;
            _matchReplies.Remove(key);
            batch = list;
        }

        var keep = new List<PendingMatchReply>();
        foreach (var item in batch)
        {
            if (item.IsExpired)
                continue;
            if (_match.SendServerChat(item.Text, nick: nick, alsoLastChanceIp: item.Address) > 0)
                Console.WriteLine($"[chat] Server → {nick} (match, отложенно): {item.Text}");
            else
                keep.Add(item);
        }

        if (keep.Count == 0)
            return;

        lock (_replyGate)
        {
            if (_matchReplies.TryGetValue(key, out var newer) && newer.Count > 0)
                keep.AddRange(newer);
            _matchReplies[key] = keep;
        }
    }
}
