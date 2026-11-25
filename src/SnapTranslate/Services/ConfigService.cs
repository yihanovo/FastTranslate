using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SnapTranslate.Services;

/// <summary>配置文件模型（设计文档 §5.4 config.json）</summary>
public sealed class AppConfig
{
    [JsonPropertyName("version")] public int Version { get; set; } = 1;

    [JsonPropertyName("baidu")] public BaiduConfig? Baidu { get; set; }

    [JsonPropertyName("hotkey")] public HotkeyConfig? Hotkey { get; set; }

    [JsonPropertyName("behavior")] public BehaviorConfig? Behavior { get; set; }

    /// <summary>翻译引擎：Auto（默认，百度优先）/ Baidu / MyMemory</summary>
    [JsonPropertyName("engine")] public int Engine { get; set; } = (int)TranslationEngine.Auto;

    [JsonPropertyName("mymemory")] public MyMemoryConfig? MyMemory { get; set; }
}

public sealed class HotkeyConfig
{
    /// <summary>主键名，如 "F1"、"A"、"D1"</summary>
    [JsonPropertyName("key")] public string Key { get; set; } = "F1";

    /// <summary>修饰键组合：Ctrl, Alt, Shift, Windows（逗号分隔）</summary>
    [JsonPropertyName("modifiers")] public List<string> Modifiers { get; set; } = new();
}

/// <summary>解析后的热键</summary>
public sealed record HotkeyBinding(
    System.Windows.Input.Key Key,
    System.Windows.Input.ModifierKeys Modifiers)
{
    public string Display => Modifiers == System.Windows.Input.ModifierKeys.None
        ? Key.ToString()
        : $"{Modifiers.ToString().Replace(", ", "+")}+{Key}";

    public static HotkeyBinding Default => new(System.Windows.Input.Key.F1, System.Windows.Input.ModifierKeys.None);
}

public sealed class BehaviorConfig
{
    [JsonPropertyName("autoStartWithWindows")] public bool AutoStartWithWindows { get; set; }

    /// <summary>新一轮截图时自动关闭已存在的翻译浮窗（防堆屏）</summary>
    [JsonPropertyName("closePreviousPin")] public bool ClosePreviousPin { get; set; } = true;

    /// <summary>热键总开关：禁用时让出全局快捷键给其他应用</summary>
    [JsonPropertyName("hotkeyEnabled")] public bool HotkeyEnabled { get; set; } = true;
}

/// <summary>翻译引擎选择：Auto = 百度已配置用百度，否则 MyMemory</summary>
public enum TranslationEngine
{
    Auto = 0,
    Baidu = 1,
    MyMemory = 2,
}

public sealed class MyMemoryConfig
{
    /// <summary>提额邮箱（de 参数）：5000 → 50000 字/天，可选</summary>
    [JsonPropertyName("email")] public string Email { get; set; } = string.Empty;
}

public sealed class BaiduConfig
{
    [JsonPropertyName("appid")] public string AppId { get; set; } = string.Empty;

    /// <summary>DPAPI(CurrentUser) 加密后 Base64；绝不明文落盘</summary>
    [JsonPropertyName("secretEnc")] public string SecretEnc { get; set; } = string.Empty;
}

public sealed record BaiduCredentials(string AppId, string Secret)
{
    public bool IsConfigured => !string.IsNullOrEmpty(AppId) && !string.IsNullOrEmpty(Secret);
}

/// <summary>
/// 配置服务：%APPDATA%\SnapTranslate\config.json
/// 密钥用 DPAPI CurrentUser 加密（设计文档 §4.8），仅本机本用户可解密。
/// </summary>
public sealed class ConfigService
{
    private readonly string _dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SnapTranslate");
    private string ConfigPath => Path.Combine(_dir, "config.json");
    private readonly object _sync = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public BaiduCredentials GetBaidu()
    {
        lock (_sync)
        {
            var cfg = Load();
            if (cfg?.Baidu is not { } b) return new BaiduCredentials(string.Empty, string.Empty);

            string secret;
            try
            {
                var raw = ProtectedData.Unprotect(Convert.FromBase64String(b.SecretEnc), null, DataProtectionScope.CurrentUser);
                secret = System.Text.Encoding.UTF8.GetString(raw);
            }
            catch (Exception ex)
            {
                Logger.Error("解密百度密钥失败", ex);
                secret = string.Empty;
            }
            return new BaiduCredentials(b.AppId, secret);
        }
    }

    public void SetBaidu(string appId, string secret)
    {
        lock (_sync)
        {
            var enc = Convert.ToBase64String(
                ProtectedData.Protect(System.Text.Encoding.UTF8.GetBytes(secret), null, DataProtectionScope.CurrentUser));

            var cfg = Load() ?? new AppConfig();
            cfg.Baidu = new BaiduConfig { AppId = appId, SecretEnc = enc };
            Save(cfg);
        }
    }

    /// <summary>读取热键绑定（缺省/非法时回退 F1）</summary>
    public HotkeyBinding GetHotkey()
    {
        lock (_sync)
        {
            var cfg = Load();
            var h = cfg?.Hotkey;
            if (h is null || string.IsNullOrEmpty(h.Key)) return HotkeyBinding.Default;

            if (!Enum.TryParse<System.Windows.Input.Key>(h.Key, ignoreCase: false, out var key)
                || key < System.Windows.Input.Key.None)
                return HotkeyBinding.Default;

            var mods = System.Windows.Input.ModifierKeys.None;
            foreach (var m in h.Modifiers)
            {
                if (Enum.TryParse<System.Windows.Input.ModifierKeys>(m, ignoreCase: true, out var parsed))
                    mods |= parsed;
            }
            return new HotkeyBinding(key, mods);
        }
    }

    public void SetHotkey(HotkeyBinding binding)
    {
        lock (_sync)
        {
            var cfg = Load() ?? new AppConfig();
            cfg.Hotkey = new HotkeyConfig
            {
                Key = binding.Key.ToString(),
                Modifiers = binding.Modifiers == System.Windows.Input.ModifierKeys.None
                    ? new List<string>()
                    : binding.Modifiers.ToString().Split(',').Select(s => s.Trim()).ToList(),
            };
            Save(cfg);
        }
    }

    public bool GetAutoStart()
    {
        lock (_sync)
        {
            return Load()?.Behavior?.AutoStartWithWindows ?? false;
        }
    }

    public void SetAutoStart(bool enabled)
    {
        lock (_sync)
        {
            var cfg = Load() ?? new AppConfig();
            cfg.Behavior ??= new BehaviorConfig();
            cfg.Behavior.AutoStartWithWindows = enabled;
            Save(cfg);
        }
    }

    public bool GetClosePreviousPin()
    {
        lock (_sync)
        {
            return Load()?.Behavior?.ClosePreviousPin ?? true;   // 默认开启
        }
    }

    public bool GetHotkeyEnabled()
    {
        lock (_sync)
        {
            return Load()?.Behavior?.HotkeyEnabled ?? true;      // 默认开启
        }
    }

    /// <summary>保存行为配置（浮窗策略/热键开关等一次写入）</summary>
    public void SetBehavior(bool autoStart, bool closePreviousPin, bool hotkeyEnabled)
    {
        lock (_sync)
        {
            var cfg = Load() ?? new AppConfig();
            cfg.Behavior = new BehaviorConfig
            {
                AutoStartWithWindows = autoStart,
                ClosePreviousPin = closePreviousPin,
                HotkeyEnabled = hotkeyEnabled,
            };
            Save(cfg);
        }
    }

    /// <summary>当前翻译引擎选择</summary>
    public TranslationEngine GetEngine()
    {
        lock (_sync)
        {
            var v = Load()?.Engine ?? 0;
            return v is >= 0 and <= 2 ? (TranslationEngine)v : TranslationEngine.Auto;
        }
    }

    public void SetEngine(TranslationEngine engine)
    {
        lock (_sync)
        {
            var cfg = Load() ?? new AppConfig();
            cfg.Engine = (int)engine;
            Save(cfg);
        }
    }

    public string GetMyMemoryEmail()
    {
        lock (_sync)
        {
            return Load()?.MyMemory?.Email ?? string.Empty;
        }
    }

    public void SetMyMemoryEmail(string email)
    {
        lock (_sync)
        {
            var cfg = Load() ?? new AppConfig();
            cfg.MyMemory = new MyMemoryConfig { Email = email?.Trim() ?? string.Empty };
            Save(cfg);
        }
    }

    private AppConfig? Load()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return null;
            var json = File.ReadAllText(ConfigPath);
            return JsonSerializer.Deserialize<AppConfig>(json, JsonOpts);
        }
        catch (Exception ex)
        {
            Logger.Error("读取配置失败", ex);
            return null;
        }
    }

    private void Save(AppConfig cfg)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(cfg, JsonOpts));
        }
        catch (Exception ex)
        {
            Logger.Error("写入配置失败", ex);
        }
    }
}
