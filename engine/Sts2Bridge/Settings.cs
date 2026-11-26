using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sts2Bridge;

/// <summary>
/// Runtime configuration for Sts2Bridge v1.1.0. Loaded from and persisted to bridge_config.json
/// next to Sts2Bridge.dll, with live synchronization between the STS2 Modding Screen UI and In-Run HUD.
/// </summary>
public sealed class BridgeConfig
{
    [JsonPropertyName("host")]
    public string Host { get; set; } = "127.0.0.1";

    [JsonPropertyName("port")]
    public int Port { get; set; } = 18765;

    /// <summary>Inference backend: "auto" (TCP if connected, else built-in .s2nn), "builtin", or "tcp".</summary>
    [JsonPropertyName("backend")]
    public string Backend { get; set; } = "auto";

    [JsonPropertyName("top_k")]
    public int TopK { get; set; } = 3;

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

    public event Action? Changed;

    public static string ModDir { get; private set; } = AppContext.BaseDirectory;

    static readonly JsonSerializerOptions SaveJsonOpts = new()
    {
        WriteIndented = true,
    };

    public void Normalize()
    {
        TopK = Math.Clamp(TopK, 1, 10);
        ActionDelayMs = Math.Clamp(ActionDelayMs, 50, 2000);
        StabilizeMs = Math.Clamp(StabilizeMs, 30, 1000);
        string b = (Backend ?? "auto").Trim().ToLowerInvariant();
        Backend = b is "builtin" or "tcp" or "auto" ? b : "auto";
    }

    public void CycleBackend()
    {
        Backend = Backend switch
        {
            "auto" => "builtin",
            "builtin" => "tcp",
            _ => "auto",
        };
        SaveAndNotify();
    }

    public string BackendDisplayLabel() => Backend switch
    {
        "builtin" => "仅内置模型 (免配置)",
        "tcp" => "仅外部服务 (Python)",
        _ => "自动 (内置兜底/外部优先)",
    };

    public void SaveAndNotify()
    {
        Normalize();
        Save();
        try { Changed?.Invoke(); } catch { }
    }

    public void Save()
    {
        try
        {
            Normalize();
            string json = JsonSerializer.Serialize(this, SaveJsonOpts);
            string cfgPath = Path.Combine(ModDir, "bridge_config.json");
            try
            {
                File.WriteAllText(cfgPath, json);
            }
            catch
            {
                string userCfg = Path.Combine(Godot.OS.GetUserDataDir(), "sts2_bridge_config.json");
                File.WriteAllText(userCfg, json);
            }
        }
        catch (Exception e)
        {
            BridgeLog.Warn($"Failed to save bridge_config.json: {e.Message}");
        }
    }

    public static BridgeConfig Load()
    {
        var asmLoc = typeof(BridgeConfig).Assembly.Location;
        if (!string.IsNullOrEmpty(asmLoc) && Path.GetDirectoryName(asmLoc) is { Length: > 0 } dir)
            ModDir = dir;

        var cfg = new BridgeConfig();
        var cfgPath = Path.Combine(ModDir, "bridge_config.json");
        string userCfgPath = "";
        try { userCfgPath = Path.Combine(Godot.OS.GetUserDataDir(), "sts2_bridge_config.json"); } catch { }
        string loadPath = (!string.IsNullOrEmpty(userCfgPath) && File.Exists(userCfgPath) && !File.Exists(cfgPath))
            ? userCfgPath
            : cfgPath;
        if (File.Exists(loadPath))
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<BridgeConfig>(File.ReadAllText(loadPath));
                if (loaded != null) cfg = loaded;
            }
            catch (Exception e)
            {
                BridgeLog.Warn($"Failed to parse {loadPath}: {e.Message}; using defaults.");
            }
        }

        if (Environment.GetEnvironmentVariable("STS2_BRIDGE_HOST") is { Length: > 0 } envHost)
            cfg.Host = envHost.Trim();
        if (int.TryParse(Environment.GetEnvironmentVariable("STS2_BRIDGE_PORT"), out int envPort) && envPort > 0)
            cfg.Port = envPort;
        if (Environment.GetEnvironmentVariable("STS2_BRIDGE_BACKEND") is { Length: > 0 } envBackend)
            cfg.Backend = envBackend.Trim();
        if (int.TryParse(Environment.GetEnvironmentVariable("STS2_BRIDGE_TOPK"), out int envTopK))
            cfg.TopK = envTopK;
        if (Environment.GetEnvironmentVariable("STS2_BRIDGE_AUTO") is { Length: > 0 } envAuto)
            cfg.AutoPlay = envAuto is "1" or "true" or "TRUE" or "yes";
        if (int.TryParse(Environment.GetEnvironmentVariable("STS2_BRIDGE_DELAY_MS"), out int envDelay) && envDelay >= 0)
            cfg.ActionDelayMs = envDelay;

        cfg.Normalize();
        return cfg;
    }
}

internal static class BridgeLog
{
    static readonly object Sync = new();
    static string? _logPath;
    static string? _userLogPath;

    public static string ActiveLogPath => _userLogPath ?? _logPath ?? "";

    public static void Init(string modDir)
    {
        _logPath = Path.Combine(modDir, "sts2_bridge.log");
        try
        {
            string udir = Godot.OS.GetUserDataDir();
            if (!string.IsNullOrEmpty(udir))
            {
                Directory.CreateDirectory(udir);
                _userLogPath = Path.Combine(udir, "sts2_bridge.log");
            }
        }
        catch { }
    }

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg) => Write("ERROR", msg);

    static void Write(string level, string msg)
    {
        string line = $"[{DateTime.Now:HH:mm:ss.fff}] [Sts2Bridge/{level}] {msg}";
        Console.Error.WriteLine(line);
        try { Godot.GD.Print(line); } catch { }
        lock (Sync)
        {
            if (_logPath != null)
            {
                try { File.AppendAllText(_logPath, line + Environment.NewLine); } catch { }
            }
            if (_userLogPath != null && !string.Equals(_userLogPath, _logPath, StringComparison.Ordinal))
            {
                try { File.AppendAllText(_userLogPath, line + Environment.NewLine); } catch { }
            }
        }
    }
}
