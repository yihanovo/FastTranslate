using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using SnapTranslate.Services;

namespace SnapTranslate.Views;

/// <summary>
/// 设置窗口：卡片式分组（快捷键/浮窗行为/通用）。
/// - 热键冲突在保存时通过真实 RegisterHotKey 验证
/// - "禁用快捷键"保存时即时注销/恢复注册
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly ConfigService _config;
    private readonly HotkeyService _hotkey;

    private HotkeyBinding _binding;
    private bool _recording;

    public SettingsWindow(ConfigService config, HotkeyService hotkey)
    {
        InitializeComponent();
        _config = config;
        _hotkey = hotkey;

        _binding = _config.GetHotkey();
        HotkeyText.Text = _binding.Display;
        CbHotkeyEnabled.IsChecked = _config.GetHotkeyEnabled();
        CbClosePreviousPin.IsChecked = _config.GetClosePreviousPin();
        CbAutoStart.IsChecked = _config.GetAutoStart();

        // 翻译引擎
        CbEngine.SelectedIndex = (int)_config.GetEngine();
        var baidu = _config.GetBaidu();
        TbBaiduAppId.Text = baidu.AppId;
        TbBaiduSecret.Text = baidu.IsConfigured ? "（已配置，留空保持不变）" : string.Empty;
        TbMyMemoryEmail.Text = _config.GetMyMemoryEmail();
        UpdateEngineHint();

        // 录制：点击热键框进入监听
        HotkeyBorder.MouseLeftButtonUp += (_, _) => StartRecording();
        PreviewKeyDown += OnPreviewKeyDown;
        CbEngine.SelectionChanged += (_, _) => UpdateEngineHint();
    }

    /// <summary>引擎卡片底部的状态提示</summary>
    private void UpdateEngineHint()
    {
        var baiduConfigured = _config.GetBaidu().IsConfigured;
        var selected = CbEngine.SelectedItem is ComboBoxItem { Tag: string t } && int.TryParse(t, out var v)
            ? (TranslationEngine)v
            : TranslationEngine.Auto;

        EngineHint.Text = selected switch
        {
            TranslationEngine.MyMemory => "当前选择：MyMemory（免注册直接用）",
            TranslationEngine.Baidu => baiduConfigured
                ? "当前选择：百度翻译（凭据已配置）"
                : "当前选择：百度翻译（⚠ 下方凭据未填写，翻译会失败）",
            _ => baiduConfigured
                ? "自动模式：将使用百度翻译"
                : "自动模式：将使用 MyMemory 免费引擎",
        };
    }

    private void StartRecording()
    {
        _recording = true;
        HotkeyText.Text = "按下组合键…";
        HotkeyText.Foreground = System.Windows.Media.Brushes.Gray;
        HotkeyBorder.BorderBrush = System.Windows.Media.Brushes.DodgerBlue;
        HotkeyError.Text = string.Empty;
    }

    private void StopRecording()
    {
        _recording = false;
        HotkeyText.Text = _binding.Display;
        HotkeyText.Foreground = System.Windows.Media.Brushes.DodgerBlue;
        HotkeyBorder.BorderBrush = System.Windows.Media.Brushes.Gray;
    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!_recording) return;
        e.Handled = true;

        // Esc：取消录制（不作为热键）
        if (e.Key == Key.Escape)
        {
            StopRecording();
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;

        // 过滤：纯修饰键按下不算完成
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin
            or Key.System or Key.ImeProcessed)
        {
            return;
        }

        // 校验：可注册的主键（排除 Tab/Space 等系统键）
        if (!IsRegistrableKey(key))
        {
            HotkeyError.Text = $"不支持按键 {key} 作为主键，请换一个组合。";
            return;
        }

        _binding = new HotkeyBinding(key, mods);
        HotkeyError.Text = string.Empty;
        StopRecording();
    }

    private static bool IsRegistrableKey(Key key) => key switch
    {
        Key.Tab or Key.Space or Key.CapsLock or Key.NumLock or Key.Scroll
            or Key.PrintScreen or Key.Pause or Key.Apps => false,
        _ => key >= Key.None,
    };

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        _binding = HotkeyBinding.Default;
        HotkeyError.Text = string.Empty;
        StopRecording();
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var hotkeyEnabled = CbHotkeyEnabled.IsChecked == true;
        var current = _config.GetHotkey();

        // 1) 热键变更/启用：先试注册，冲突则拒绝保存
        if (hotkeyEnabled && _binding != current)
        {
            try
            {
                _hotkey.Register(_binding.Key, _binding.Modifiers);
            }
            catch (InvalidOperationException ex)
            {
                // 回滚到当前生效热键
                try
                {
                    if (_hotkey.IsEnabled) _hotkey.Register(current.Key, current.Modifiers);
                    else _hotkey.Unregister();
                }
                catch { /* 回滚失败：下次启动按配置注册 */ }

                HotkeyError.Text = ex.Message;
                System.Windows.MessageBox.Show($"热键注册失败，请换一个组合。\n\n{ex.Message}", "SnapTranslate",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        else if (hotkeyEnabled && !_hotkey.IsEnabled)
        {
            // 从禁用切到启用：按绑定注册
            try { _hotkey.Enable(); }
            catch (InvalidOperationException ex)
            {
                HotkeyError.Text = ex.Message;
                System.Windows.MessageBox.Show($"热键启用失败：\n\n{ex.Message}", "SnapTranslate",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        else if (!hotkeyEnabled && _hotkey.IsEnabled)
        {
            // 从启用切到禁用：即时注销（让出快捷键）
            _hotkey.Disable();
        }

        // 2) 翻译引擎 + 百度凭据（密钥留空 = 保持已配置值不变）
        var engine = CbEngine.SelectedItem is ComboBoxItem { Tag: string t } && int.TryParse(t, out var v)
            ? (TranslationEngine)v
            : TranslationEngine.Auto;
        _config.SetEngine(engine);

        var appId = TbBaiduAppId.Text.Trim();
        var secret = TbBaiduSecret.Text.Trim();
        var placeholder = secret.StartsWith("（已配置");
        if (appId.Length > 0 && secret.Length > 0 && !placeholder)
        {
            _config.SetBaidu(appId, secret);   // DPAPI 加密落盘
        }

        // MyMemory 提额邮箱（可选）
        _config.SetMyMemoryEmail(TbMyMemoryEmail.Text);

        // 3) 行为/热键配置
        _config.SetHotkey(_binding);
        _config.SetBehavior(
            autoStart: CbAutoStart.IsChecked == true,
            closePreviousPin: CbClosePreviousPin.IsChecked == true,
            hotkeyEnabled: hotkeyEnabled);
        ApplyAutoStart(CbAutoStart.IsChecked == true);

        Logger.Info($"设置已保存：引擎={engine}, 热键={_binding.Display} 启用={_hotkey.IsEnabled}, 浮窗自动关={CbClosePreviousPin.IsChecked}, 自启={CbAutoStart.IsChecked}");

        Close();
    }

    /// <summary>HKCU Run 键：免管理员权限的开机自启（设计文档 §4.9）</summary>
    internal static void ApplyAutoStart(bool enabled)
    {
        const string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string valueName = "SnapTranslate";
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(runKey, writable: true);
            if (key is null) return;

            if (enabled)
            {
                // 单文件发布下 Assembly.Location 为空，必须用 ProcessPath / BaseDirectory
                var exePath = Environment.ProcessPath ?? AppContext.BaseDirectory;
                key.SetValue(valueName, $"\"{exePath}\"");
            }
            else
            {
                key.DeleteValue(valueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            Logger.Error("写入开机自启失败", ex);
        }
    }
}
