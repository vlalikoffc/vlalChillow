using StandChillow.LanServer.Config;

namespace StandChillow.LanServer.Net;

/// <summary>
/// Серверная логика запуска матча: какой режим активен и кто отметился <c>/ready</c>.
/// </summary>
/// <remarks>
/// Здесь нет ни одного пакета и ни одного обращения к сети — только решение
/// «стартовать или нет». Отправкой занимается <see cref="GameNetHost"/> через уже
/// существующий <c>ArmMatchStart</c>. Поведение клиентов не затрагивается.
/// </remarks>
public sealed class MatchStartGate
{
    private readonly object _gate = new();
    private readonly HashSet<string> _ready = new(StringComparer.OrdinalIgnoreCase);

    private MatchStartMode _mode = MatchStartMode.Manual;
    private int _minPlayersPerTeam = 1;
    private bool _announceInChat = true;
    private bool _resetOnLeave = true;
    private bool _allowUnready = true;

    public MatchStartMode Mode
    {
        get { lock (_gate) return _mode; }
    }

    public bool AnnounceInChat
    {
        get { lock (_gate) return _announceInChat; }
    }

    public bool AllowUnready
    {
        get { lock (_gate) return _allowUnready; }
    }

    /// <summary>Перечитывает настройки из конфига (например, после <c>config reload</c>).</summary>
    public void Apply(ServerConfig config)
    {
        lock (_gate)
        {
            _mode = config.StartMode;
            _minPlayersPerTeam = config.MatchStart.Auto.MinPlayersPerTeam;
            _announceInChat = config.MatchStart.Ready.AnnounceInChat;
            _resetOnLeave = config.MatchStart.Ready.ResetOnLeave;
            _allowUnready = config.MatchStart.Ready.AllowUnready;
        }
    }

    /// <summary>Сбрасывает все голоса (новая катка, конец матча).</summary>
    public void Clear()
    {
        lock (_gate) _ready.Clear();
    }

    /// <summary>Убирает голос игрока (вышел из катки). true — что-то реально убрали.</summary>
    public bool Remove(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        lock (_gate) return _ready.Remove(name.Trim());
    }

    public bool IsReady(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        lock (_gate) return _ready.Contains(name.Trim());
    }

    /// <summary>true — состояние изменилось (впервые нажал /ready).</summary>
    public bool MarkReady(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        lock (_gate) return _ready.Add(name.Trim());
    }

    /// <summary>true — голос действительно был и снят.</summary>
    public bool MarkUnready(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        lock (_gate) return _ready.Remove(name.Trim());
    }

    /// <summary>Сколько голосов сейчас «за».</summary>
    public int ReadyCount
    {
        get { lock (_gate) return _ready.Count; }
    }

    /// <summary>
    /// Решает, пора ли стартовать. Чистая функция — удобно тестить без сокетов.
    /// </summary>
    /// <param name="inMatchPlayers">Ники тех, кто сейчас реально в катке.</param>
    /// <param name="ctCount">Живых игроков за CT.</param>
    /// <param name="trCount">Живых игроков за TR.</param>
    /// <param name="reason">Человекочитаемая причина решения.</param>
    public bool ShouldStart(
        IReadOnlyList<string> inMatchPlayers,
        int ctCount,
        int trCount,
        out string reason)
    {
        MatchStartMode mode;
        int minPerTeam;
        lock (_gate)
        {
            mode = _mode;
            minPerTeam = _minPlayersPerTeam;
        }

        if (mode == MatchStartMode.Auto)
        {
            if (ctCount >= minPerTeam && trCount >= minPerTeam)
            {
                reason = $"auto: CT={ctCount} TR={trCount} (нужно по {minPerTeam})";
                return true;
            }

            reason = $"auto: CT={ctCount} TR={trCount}, нужно по {minPerTeam}";
            return false;
        }

        if (mode == MatchStartMode.Ready)
        {
            var total = inMatchPlayers?.Count ?? 0;
            if (total == 0)
            {
                reason = "ready: в катке никого нет";
                return false;
            }

            var missing = 0;
            lock (_gate)
            {
                foreach (var nick in inMatchPlayers!)
                {
                    if (string.IsNullOrWhiteSpace(nick))
                        continue;
                    if (!_ready.Contains(nick.Trim()))
                        missing++;
                }
            }

            if (missing == 0)
            {
                reason = $"ready: готовы все ({total}/{total})";
                return true;
            }

            reason = $"ready: готовы {total - missing}/{total}";
            return false;
        }

        reason = "manual: ждём /start или /set start";
        return false;
    }
}
