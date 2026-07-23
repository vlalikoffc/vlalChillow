using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using StandChillow.Server.Plugins;

namespace StandChillow.Plugin.Telegram;

/// <summary>
/// HTTP client connector to the existing Python tgbot standchillow ingest.
/// tgbot listens on <c>POST http://127.0.0.1:5267/state</c> (schema <c>standchillow.status.v1</c>);
/// this plugin only pushes snapshots — it does <b>not</b> run Telegram Bot API / tokens.
/// Contract: <c>/home/vlal/tgbot/deploy/STANDCHILLOW_TELEMETRY.md</c>
/// and <c>/home/vlal/tgbot/plugins/standchillow.py</c>.
/// </summary>
public sealed class TelegramPlugin : IServerPlugin
{
    public string Name => "Telegram";
    public string? Version => "1.0.0";

    private IPluginContext? _ctx;
    private HttpClient? _http;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private TelegramConfig _config = new();
    private int _failStreak;
    private DateTime _lastOkLogUtc = DateTime.MinValue;

    public void OnLoad(IPluginContext context)
    {
        _ctx = context;
        _config = TelegramConfig.Load(context);
        EnsureExampleConfig(context.DataDirectory);

        if (!_config.Enabled)
        {
            context.Log.Info("disabled (config.enabled=false) — not connecting to tgbot ingest");
            return;
        }

        _http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(_config.TimeoutMs) };
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => PushLoopAsync(_cts.Token));

        context.RegisterConsoleCommand("tgstatus", ctx =>
        {
            ctx.Reply(
                $"tgbot connector → POST {_config.Url} every {_config.IntervalMs}ms " +
                $"(enabled={_config.Enabled}, failStreak={_failStreak})");
            return true;
        }, "Show tgbot status-pusher settings");

        context.Log.Info(
            $"connecting to tgbot ingest {_config.Url} every {_config.IntervalMs}ms " +
            "(counterpart to plugins/standchillow.py — not a second bot)");
    }

    public void OnUnload()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
        _http?.Dispose();
        _cts?.Dispose();
        _http = null;
        _cts = null;
        _loop = null;
        _ctx?.Log.Info("unloaded");
    }

    private async Task PushLoopAsync(CancellationToken ct)
    {
        var log = _ctx!.Log;
        // Small delay so AttachHost has a game before first POST.
        try { await Task.Delay(500, ct); } catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PushOnceAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _failStreak++;
                if (_failStreak <= 3 || _failStreak % 30 == 0)
                    log.Warn($"POST failed (#{_failStreak}): {ex.Message}");
            }

            try { await Task.Delay(_config.IntervalMs, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task PushOnceAsync(CancellationToken ct)
    {
        var ctx = _ctx;
        var http = _http;
        if (ctx is null || http is null) return;

        var snap = ctx.Host.GetStatus();
        using var response = await http.PostAsJsonAsync(
            _config.Url,
            snap,
            StatusJsonOptions,
            ct);

        if (!response.IsSuccessStatusCode)
        {
            _failStreak++;
            var body = await response.Content.ReadAsStringAsync(ct);
            if (_failStreak <= 3 || _failStreak % 30 == 0)
            {
                ctx.Log.Warn(
                    $"HTTP {(int)response.StatusCode} from {_config.Url}: {Trim(body, 120)}");
            }
            return;
        }

        if (_failStreak > 0)
            ctx.Log.Info($"POST ok again after {_failStreak} failures");
        _failStreak = 0;

        // Quiet success — log at most once per minute.
        var now = DateTime.UtcNow;
        if ((now - _lastOkLogUtc).TotalSeconds >= 60)
        {
            _lastOkLogUtc = now;
            ctx.Log.Debug(
                $"ok match={snap.MatchStarted} mode={snap.Mode.Id} map={snap.Map} " +
                $"roster={snap.LobbyRoster.Count}");
        }
    }

    private static readonly JsonSerializerOptions StatusJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private static void EnsureExampleConfig(string dataDir)
    {
        var example = Path.Combine(dataDir, "config.example.json");
        if (File.Exists(example)) return;
        try
        {
            File.WriteAllText(example, """
{
  "url": "http://127.0.0.1:5267/state",
  "interval_ms": 2000,
  "enabled": true,
  "timeout_ms": 1500
}
""");
        }
        catch { /* ignore */ }
    }

    private static string Trim(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}

internal sealed class TelegramConfig
{
    public string Url { get; set; } = "http://127.0.0.1:5267/state";
    public int IntervalMs { get; set; } = 2000;
    public bool Enabled { get; set; } = true;
    public int TimeoutMs { get; set; } = 1500;

    public static TelegramConfig Load(IPluginContext ctx)
    {
        var cfg = new TelegramConfig();

        // Env overrides (same names as STANDCHILLOW_TELEMETRY.md).
        var envUrl = Environment.GetEnvironmentVariable("STANDCHILLOW_TG_URL");
        if (!string.IsNullOrWhiteSpace(envUrl))
            cfg.Url = envUrl.Trim();

        var envInterval = Environment.GetEnvironmentVariable("STANDCHILLOW_TG_INTERVAL_MS");
        if (int.TryParse(envInterval, out var ms) && ms >= 500)
            cfg.IntervalMs = ms;

        var envEnabled = Environment.GetEnvironmentVariable("STANDCHILLOW_TG_ENABLED");
        if (!string.IsNullOrWhiteSpace(envEnabled))
            cfg.Enabled = IsTruthy(envEnabled);

        var path = Path.Combine(ctx.DataDirectory, "config.json");
        if (!File.Exists(path))
        {
            // Seed a real config from defaults so operators can edit without copying the example.
            try
            {
                File.WriteAllText(path, JsonSerializer.Serialize(new
                {
                    url = cfg.Url,
                    interval_ms = cfg.IntervalMs,
                    enabled = cfg.Enabled,
                    timeout_ms = cfg.TimeoutMs,
                }, new JsonSerializerOptions { WriteIndented = true }));
                ctx.Log.Info($"wrote default {path}");
            }
            catch (Exception ex)
            {
                ctx.Log.Warn($"could not write {path}: {ex.Message}");
            }
            return cfg;
        }

        try
        {
            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("url", out var u) && u.GetString() is { } url && url.Length > 0)
                cfg.Url = url;
            if (root.TryGetProperty("interval_ms", out var i) && i.TryGetInt32(out var interval) && interval >= 500)
                cfg.IntervalMs = interval;
            if (root.TryGetProperty("enabled", out var e))
            {
                if (e.ValueKind == JsonValueKind.False) cfg.Enabled = false;
                if (e.ValueKind == JsonValueKind.True) cfg.Enabled = true;
            }
            if (root.TryGetProperty("timeout_ms", out var t) && t.TryGetInt32(out var timeout) && timeout >= 200)
                cfg.TimeoutMs = timeout;
            ctx.Log.Info($"config ← {path}");
        }
        catch (Exception ex)
        {
            ctx.Log.Warn($"bad config.json ({ex.Message}) — using defaults/env");
        }

        // Env still wins over file for URL/interval/enabled when set.
        if (!string.IsNullOrWhiteSpace(envUrl))
            cfg.Url = envUrl!.Trim();
        if (int.TryParse(envInterval, out ms) && ms >= 500)
            cfg.IntervalMs = ms;
        if (!string.IsNullOrWhiteSpace(envEnabled))
            cfg.Enabled = IsTruthy(envEnabled!);

        return cfg;
    }

    private static bool IsTruthy(string v) =>
        v.Trim() is "1" or "true" or "TRUE" or "True" or "yes" or "YES" or "Yes" or "on" or "ON" or "On";
}
