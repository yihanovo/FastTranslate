using System.Windows;
using System.Windows.Input;
using Hardcodet.Wpf.TaskbarNotification;
using SnapTranslate.Pipelines;
using SnapTranslate.Services;

namespace SnapTranslate;

public partial class App : System.Windows.Application
{
    private TaskbarIcon? _tray;
    private SelectionCoordinator? _coordinator;
    private SingleInstanceGuard? _instanceGuard;
    private bool _languagePackMissing;
    private HotkeyService? _hotkey;
    private ConfigService? _config;
    private HotkeyBinding? _activeBinding;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 单实例：二次启动直接退出（设计文档 §4.9）
        _instanceGuard = new SingleInstanceGuard("SnapTranslate.SingleInstance");
        if (!_instanceGuard.IsFirstInstance)
        {
            Shutdown(1);
            return;
        }

        Logger.Info($"========== SnapTranslate 启动 v{typeof(App).Assembly.GetName().Version} ==========");
        Logger.Info($"OS: {Environment.OSVersion}, 64位: {Environment.Is64BitProcess}");

        // OCR 语言包检测（设计文档 §7 风险 #3）：缺失则气泡引导，不阻断启动
        if (!OcrService.IsEnglishLanguagePackInstalled())
        {
            Logger.Info("检测到系统缺少英文 OCR 语言包");
            _languagePackMissing = true;
        }

        // 组装主链路：热键 → 截图 → 框选 → OCR → 翻译 → 钉屏（设计文档 §3.1）
        var hotkey = new HotkeyService();
        var capture = new ScreenCaptureService();
        var overlay = new OverlayManager();
        var ocr = new OcrService();
        var config = new ConfigService();
        var translator = new TranslationRouter(
            config,
            new BaiduTranslationProvider(config),
            new MyMemoryTranslationProvider(config));
        _coordinator = new SelectionCoordinator(hotkey, capture, overlay, ocr, translator, config);
        _hotkey = hotkey;
        _config = config;

        // 按用户配置注册热键（缺省 F1）；用户禁用则不注册（让出快捷键给其他应用）
        var binding = config.GetHotkey();
        if (config.GetHotkeyEnabled())
        {
            try
            {
                hotkey.Register(binding.Key, binding.Modifiers);
            }
            catch (InvalidOperationException ex)
            {
                Logger.Error("启动热键注册失败，回退 F1 尝试", ex);
                try
                {
                    hotkey.Register(Key.F1, ModifierKeys.None);
                    binding = HotkeyBinding.Default;
                }
                catch (InvalidOperationException)
                {
                    binding = null!; // 标记无热键：仅托盘左键可用
                }
            }
        }
        else
        {
            // 用户禁用：不注册即未启用（未注册状态下 IsEnabled 应为 false）
            hotkey.Disable();
        }
        _activeBinding = binding;

        // 凭据状态诊断（密钥用 DPAPI 加密存储于 config.json，见 ConfigService）
        if (!config.GetBaidu().IsConfigured)
        {
            Logger.Info("百度翻译凭据未配置：翻译将提示 NO_CONF");
        }

        // 托盘
        _tray = new TaskbarIcon
        {
            IconSource = new System.Windows.Media.Imaging.BitmapImage(
                new Uri("pack://application:,,,/Assets/tray.ico")),
            ToolTipText = $"SnapTranslate 划屏即译 ({_activeBinding?.Display ?? "无热键-左键点击触发"})",
            ContextMenu = BuildTrayMenu(),
        };
        _tray.TrayLeftMouseUp += (_, _) => _ = _coordinator!.TriggerCaptureAsync();

        // 启动欢迎卡片：给用户"启动成功"的即时视觉反馈（不抢焦点，1.7s 自动消失）
        var splash = new Views.SplashWindow(_activeBinding?.Display ?? "托盘左键");
        splash.Show();

        // 语言包缺失气泡引导（设计文档 §7 风险 #3）
        if (_languagePackMissing)
        {
            _tray.ShowBalloonTip("SnapTranslate 提示",
                "系统缺少英文 OCR 语言包，截图后将无法识别文字。\n" +
                "设置 → 时间和语言 → 语言和区域 → 添加语言（英语）后重启本程序。",
                Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Warning);
        }

        DispatcherUnhandledException += (_, args) =>
        {
            // 全局兜底：不让异常杀死常驻进程；完整堆栈写日志（弹窗只显示摘要）
            Logger.Error("未处理异常(UI线程)", args.Exception);
            System.Windows.MessageBox.Show(
                $"发生未处理异常：{args.Exception.GetType().Name}: {args.Exception.Message}\n\n完整详情见日志：\n%APPDATA%\\SnapTranslate\\app.log",
                "SnapTranslate",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            args.Handled = true;
        };
    }

    private System.Windows.Controls.ContextMenu BuildTrayMenu()
    {
        var menu = new System.Windows.Controls.ContextMenu();

        var captureItem = new System.Windows.Controls.MenuItem
        {
            Header = $"截图翻译 ({_activeBinding?.Display ?? "点击触发"})",
        };
        captureItem.Click += (_, _) => _ = _coordinator!.TriggerCaptureAsync();
        menu.Items.Add(captureItem);

        menu.Items.Add(new System.Windows.Controls.Separator());

        var settingsItem = new System.Windows.Controls.MenuItem { Header = "设置 (_S)…" };
        settingsItem.Click += (_, _) => OpenSettings();
        menu.Items.Add(settingsItem);

        menu.Items.Add(new System.Windows.Controls.Separator());

        var exitItem = new System.Windows.Controls.MenuItem { Header = "退出 (_X)" };
        exitItem.Click += (_, _) => ShutdownApp();
        menu.Items.Add(exitItem);

        return menu;
    }

    private void OpenSettings()
    {
        if (_hotkey is null || _config is null || _tray is null) return;

        var win = new Views.SettingsWindow(_config, _hotkey);
        win.Owner = null;
        win.Closed += (_, _) =>
        {
            // 设置保存后刷新托盘提示与菜单热键文案（反映启用/禁用状态）
            _activeBinding = _config.GetHotkey();
            var display = _hotkey.IsEnabled ? _activeBinding.Display : "已禁用-点击托盘触发";
            _tray.ToolTipText = $"SnapTranslate 划屏即译 ({display})";
            if (_tray.ContextMenu?.Items[0] is System.Windows.Controls.MenuItem mi)
            {
                mi.Header = $"截图翻译 ({display})";
            }
        };
        win.Show();
        win.Activate();
    }

    private void ShutdownApp()
    {
        _coordinator?.Dispose();
        _tray?.Dispose();
        _instanceGuard?.Dispose();
        Shutdown(0);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        base.OnExit(e);
    }
}
