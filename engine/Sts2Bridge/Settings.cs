using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sts2Bridge;

/// <summary>
/// Runtime configuration for Sts2Bridge. Loaded from bridge_config.json next to Sts2Bridge.dll,
/// with optional environment variable overrides (STS2_BRIDGE_HOST, STS2_BRIDGE_PORT, STS2_BRIDGE_AUTO, STS2_BRIDGE_DELAY_MS).
/// </summary>
public sealed class BridgeConfig
{
    [JsonPropertyName("host")]
    public string Host { get; set; } = "127.0.0.1";

    [JsonPropertyName("port")]
    public int Port { get; set; } = 18765;

    [JsonPropertyName("auto_play")]
    public bool AutoPlay { get; set; } = false;

    [JsonPropertyName("action_delay_ms")]
    public int ActionDelayMs { get; set; } = 250;

    [JsonPropertyName("stabilize_ms")]
    public int StabilizeMs { get; set; } = 120;

    [JsonPropertyName("route_summary")]
    public bool RouteSummary { get; set; } = true;

    [JsonPropertyName("auto_gold")]
    public bool AutoGold { get; set; } = true;

    [JsonPropertyName("auto_free_relics")]
    public bool AutoFreeRelics { get; set; } = true;

    [JsonPropertyName("show_overlay")]
    public bool ShowOverlay { get; set; } = true;

    public static string ModDir { get; private set; } = AppContext.BaseDirectory;

    public static BridgeConfig Load()
    {
        var asmLoc = typeof(BridgeConfig).Assembly.Location;
        if (!string.IsNullOrEmpty(asmLoc) && Path.GetDirectoryName(asmLoc) is { Length: > 0 } dir)
            ModDir = dir;

        var cfg = new BridgeConfig();
        var cfgPath = Path.Combine(ModDir, "bridge_config.json");
        if (File.Exists(cfgPath))
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<BridgeConfig>(File.ReadAllText(cfgPath));
                if (loaded != null) cfg = loaded;
            }
            catch (Exception e)
            {
                BridgeLog.Warn($"Failed to parse {cfgPath}: {e.Message}; using defaults.");
            }
        }

        if (Environment.GetEnvironmentVariable("STS2_BRIDGE_HOST") is { Length: > 0 } envHost)
            cfg.Host = envHost.Trim();
        if (int.TryParse(Environment.GetEnvironmentVariable("STS2_BRIDGE_PORT"), out int envPort) && envPort > 0)
            cfg.Port = envPort;
        if (Environment.GetEnvironmentVariable("STS2_BRIDGE_AUTO") is { Length: > 0 } envAuto)
            cfg.AutoPlay = envAuto is "1" or "true" or "TRUE" or "yes";
        if (int.TryParse(Environment.GetEnvironmentVariable("STS2_BRIDGE_DELAY_MS"), out int envDelay) && envDelay >= 0)
            cfg.ActionDelayMs = envDelay;

        return cfg;
    }
}

internal static class BridgeLog
{
    static readonly object Sync = new();
    static string? _logPath;

    public static void Init(string modDir)
    {
        _logPath = Path.Combine(modDir, "sts2_bridge.log");
    }

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg) => Write("ERROR", msg);

    static void Write(string level, string msg)
    {
        string line = $"[{DateTime.Now:HH:mm:ss.fff}] [Sts2Bridge/{level}] {msg}";
        Console.Error.WriteLine(line);
        try { Godot.GD.Print(line); } catch { }
        if (_logPath == null) return;
        lock (Sync)
        {
            try { File.AppendAllText(_logPath, line + Environment.NewLine); } catch { }
        }
    }
}
