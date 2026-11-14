using System.Text.Json;
using System.Text.Json.Serialization;

namespace StandChillow.LanServer.Bans;

/// <summary>
/// <c>banlist.json</c> рядом с конфигом: баны и справочник «ник ↔ id ↔ IP».
/// </summary>
/// <remarks>
/// <para>
/// Две секции в одном файле: <c>bans</c> — кто забанен, <c>players</c> — кого сервер
/// вообще видел (нужно, чтобы <c>/ban</c> работал для уже вышедшего игрока, в том
/// числе после рестарта).
/// </para>
/// <para>
/// Запись атомарная: сначала <c>banlist.json.tmp</c>, потом замена. Битый файл не
/// роняет сервер — логируется и стартует пустым, а не падает на первом входе.
/// </para>
/// </remarks>
public sealed class BanStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public BanStore(string path) => Path = path;

    /// <summary>Полный путь к файлу.</summary>
    public string Path { get; }

    /// <summary>Читает файл; отсутствие/битый JSON — пустые списки и строка в лог.</summary>
    public (List<BanRecord> Bans, List<SeenPlayer> Players) Load()
    {
        try
        {
            if (!File.Exists(Path))
                return (new List<BanRecord>(), new List<SeenPlayer>());

            var json = File.ReadAllText(Path);
            if (string.IsNullOrWhiteSpace(json))
                return (new List<BanRecord>(), new List<SeenPlayer>());

            var doc = JsonSerializer.Deserialize<FileDto>(json, JsonOptions);
            var bans = doc?.Bans ?? new List<BanRecord>();
            var players = doc?.Players ?? new List<SeenPlayer>();
            Console.WriteLine(
                $"[ban] прочитано из {Path}: банов {bans.Count}, личностей в справочнике {players.Count}");
            return (bans, players);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ban] не смог прочитать {Path}: {ex.Message} — начинаю с пустого списка");
            return (new List<BanRecord>(), new List<SeenPlayer>());
        }
    }

    /// <summary>Пишет файл атомарно. Никогда не бросает — вызывающему нужен только факт.</summary>
    public bool Save(IReadOnlyList<BanRecord> bans, IReadOnlyList<SeenPlayer> players)
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var dto = new FileDto
            {
                Version = 1,
                Bans = bans.ToList(),
                Players = players.ToList(),
            };
            var json = JsonSerializer.Serialize(dto, JsonOptions);
            var temp = Path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, Path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ban] не смог сохранить {Path}: {ex.Message}");
            return false;
        }
    }

    private sealed class FileDto
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("bans")]
        public List<BanRecord>? Bans { get; set; } = new();

        [JsonPropertyName("players")]
        public List<SeenPlayer>? Players { get; set; } = new();
    }
}
