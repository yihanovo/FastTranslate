using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using SnapTranslate.Interop;
using SnapTranslate.Models;

namespace SnapTranslate.Views;

/// <summary>
/// 钉屏结果浮窗（设计文档 §4.7）。
/// 状态机：Recognizing → Translating → Done / Failed；WS_EX_NOACTIVATE 不抢焦点。
/// </summary>
public partial class PinResultWindow : Window
{
    /// <summary>重试按钮回调（重新执行翻译链路）</summary>
    public event Action? RetryRequested;

    /// <summary>再截取按钮回调（发起新一轮截图翻译）</summary>
    public event Action? RecaptureRequested;

    private string _sourceText = string.Empty;

    public PinResultWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;

        // 不抢焦点 + 不出现在 Alt-Tab
        nint style = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE,
            style | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW);

        // 允许 Esc 关闭（无需键盘焦点：消息钩子）
        var source = HwndSource.FromHwnd(hwnd);
        source?.AddHook(WndProc);
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        // WM_KEYUP(Escape)：由于 WS_EX_NOACTIVATE 窗口不会获得键盘焦点，
        // Esc 兜底通过激活时的键盘事件处理（KeyDown 在此窗口激活时生效）
        if (msg == 0x0100 && wParam == 0x1B) // WM_KEYDOWN && VK_ESCAPE
        {
            Close();
            handled = true;
        }
        return IntPtr.Zero;
    }

    /// <summary>在选区附近显示浮窗（DIP 坐标），并开始识别流程</summary>
    public void ShowAt(Rect selectionRect)
    {
        // 初始位置：选区右侧；放不下则左侧；再放不下则选区下方
        var wa = SystemParameters.WorkArea;
        double w = Math.Min(ActualWidth == 0 ? Width : ActualWidth, 480);
        double h = ActualHeight == 0 ? 200 : ActualHeight;

        double left = selectionRect.Right + 8;
        if (left + w > wa.Right) left = selectionRect.Left - w - 8;
        if (left < wa.Left) left = wa.Left;
        double top = selectionRect.Top;
        if (top + h > wa.Bottom) top = Math.Max(wa.Top, wa.Bottom - h - 8);

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = left;
        Top = top;

        SetState(string.Empty, string.Empty);
        Show();
    }

    // ---------- 状态更新（可从任意线程调用） ----------

    public void SetOcrResult(OcrOutcome outcome)
    {
        Dispatcher.Invoke(() =>
        {
            _sourceText = outcome.FullText;
            SourceText.Text = outcome.FullText;
            // 译文区保持空白，翻译到达后填充（无"翻译中"占位）
            TargetText.Text = string.Empty;
            TargetText.Foreground = System.Windows.Media.Brushes.Gray;
            BtnRetry.Visibility = Visibility.Collapsed;
        });
    }

    public void SetOcrEmpty()
    {
        Dispatcher.Invoke(() =>
        {
            _sourceText = string.Empty;
            SourceText.Text = string.Empty;
            TargetText.Text = "选区内没有可识别的英文文本，请点击\"↺ 再截取\"重新框选。";
            MarkDone();
        });
    }

    public void SetOcrUnavailable()
    {
        Dispatcher.Invoke(() =>
        {
            _sourceText = string.Empty;
            SourceText.Text = "（OCR 引擎不可用）";
            TargetText.Text = "系统缺少英文 OCR 语言包：\n设置 → 时间和语言 → 语言和区域 → 添加语言（英语）\n安装时勾选\"基本输入\"与 OCR 组件后重启本程序。";
            MarkDone();
        });
    }

    public void SetTranslation(TranslationResult result)
    {
        Dispatcher.Invoke(() =>
        {
            if (result.Success)
            {
                TargetText.Text = result.Translated ?? string.Empty;
            }
            else
            {
                TargetText.Text = $"翻译失败：{result.ErrorMessage ?? "未知错误"}";
            }
            MarkDone();
        });
    }

    private void SetState(string source, string target)
    {
        SourceText.Text = source;
        TargetText.Text = target;
        TargetText.Foreground = System.Windows.Media.Brushes.Gray;
        BtnRetry.Visibility = Visibility.Collapsed;
    }

    private void MarkDone()
    {
        TargetText.Foreground = System.Windows.Media.Brushes.Black;
        BtnRetry.Visibility = Visibility.Visible;
    }

    // ---------- 交互 ----------

    private void OnDragBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { /* 拖动取消 */ }
            e.Handled = true;
        }
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var text = string.IsNullOrEmpty(_sourceText) ? string.Empty : $"{_sourceText}\n{TargetText.Text}";
            if (!string.IsNullOrEmpty(text)) System.Windows.Clipboard.SetText(text);
        }
        catch { /* 剪贴板被占用 */ }
    }

    private void OnRetryClick(object sender, RoutedEventArgs e)
    {
        BtnRetry.Visibility = Visibility.Collapsed;
        TargetText.Foreground = System.Windows.Media.Brushes.Gray;
        TargetText.Text = "重试中…";
        RetryRequested?.Invoke();
    }

    private void OnRecaptureClick(object sender, RoutedEventArgs e)
    {
        RecaptureRequested?.Invoke();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
