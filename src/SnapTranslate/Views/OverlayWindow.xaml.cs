using System.Drawing;
using System.Windows;
using Point = System.Windows.Point;
using WinIn = System.Windows.Input;
using WinMedia = System.Windows.Media;
using System.Windows.Controls;
using System.Windows.Interop;
using SnapTranslate.Interop;
using SnapTranslate.Services;

namespace SnapTranslate.Views;

/// <summary>
/// 全屏截图遮罩窗口（设计文档 §4.3）。
/// 坐标策略：内部交互用 WPF DIP；确认时按 冻结位图像素/窗口尺寸 比例映射回物理像素，
/// 与 DPI 无关（Image 以 Stretch=Fill 铺满整个虚拟屏）。
/// </summary>
public partial class OverlayWindow : Window
{
    private Bitmap? _frozen;
    private Rect _selection;        // DIP
    private bool _hasSelection;
    private bool _dragging;             // 正在拖出新选区
    private string? _resizeHandle;      // 正在拖动的手柄 Tag
    private Point _startPoint;
    private Point _anchor;          // 调整大小时固定的对角/对边

    private double RatioX => _frozen!.Width / RootCanvas.ActualWidth;
    private double RatioY => _frozen!.Height / RootCanvas.ActualHeight;

    /// <summary>确认选区（物理像素，虚拟屏坐标系）</summary>
    public event Action<Rectangle>? Confirmed;

    /// <summary>取消（Esc / 工具条取消 / 右键）</summary>
    public event Action? Cancelled;

    public OverlayWindow()
    {
        InitializeComponent();
        RootCanvas.MouseLeftButtonDown += OnCanvasMouseDown;
        RootCanvas.MouseMove += OnCanvasMouseMove;
        RootCanvas.MouseLeftButtonUp += OnCanvasMouseUp;
        RootCanvas.MouseRightButtonUp += (_, _) => Cancel();

        // 松手即确认模式：无需手柄事件（元素永久隐藏）
        Loaded += OnLoaded;
        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // 不出现在 Alt-Tab 列表
        var hwnd = new WindowInteropHelper(this).Handle;
        nint style = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE,
            style | NativeMethods.WS_EX_TOOLWINDOW);
    }

    /// <summary>显示遮罩：参数为已抓好的冻结帧（先抓屏后显示，顺序见设计文档 §4.2）</summary>
    public void ShowOverlay(Bitmap frozen, Rectangle virtualBounds)
    {
        _frozen = frozen;
        var hBitmap = frozen.GetHbitmap();
        try
        {
            FrozenImage.Source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap, IntPtr.Zero, Int32Rect.Empty,
                System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
        }
        finally
        {
            NativeMethods.DeleteObject(hBitmap);
        }

        // 先 Show 再用物理像素 SetWindowPos 精确覆盖虚拟屏（避免 DIP 换算误差）
        Show();
        Activate();
        var hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.SetWindowPos(hwnd, HWND_TOPMOST,
            virtualBounds.X, virtualBounds.Y, virtualBounds.Width, virtualBounds.Height,
            SWP_FRAMECHANGED);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ResetSelection();
    }

    // ---------- 鼠标：拖出新选区 ----------

    private void OnCanvasMouseDown(object sender, WinIn.MouseButtonEventArgs e)
    {
        if (_resizeHandle != null) return; // 手柄拖动由各自处理器接管

        var p = e.GetPosition(RootCanvas);

        // 已有选区时：点击选区内部不启动新拖拽（保证双击确认可用；点外部/从外部拖拽可重新框选）
        if (_hasSelection && _selection.Contains(p))
        {
            e.Handled = true;
            return;
        }

        _startPoint = p;
        _dragging = true;
        _hasSelection = true;
        RootCanvas.CaptureMouse();
        UpdateSelection(_startPoint, _startPoint);
        e.Handled = true;
    }

    private void OnCanvasMouseMove(object sender, WinIn.MouseEventArgs e)
    {
        var p = e.GetPosition(RootCanvas);
        if (_dragging)
        {
            UpdateSelection(_startPoint, p);
        }
        else if (_resizeHandle != null)
        {
            ApplyResize(p);
        }
    }

    private void OnCanvasMouseUp(object sender, WinIn.MouseButtonEventArgs e)
    {
        if (_dragging)
        {
            _dragging = false;
            RootCanvas.ReleaseMouseCapture();

            // 松手即确认（用户需求：框选完成直接翻译，无需二次确认）
            // 过小选区视为误触，忽略
            if (_selection.Width >= 4 && _selection.Height >= 4)
            {
                Confirm();
            }
            else
            {
                ResetSelection();
            }
            e.Handled = true;
        }
    }

    // ---------- 手柄：调整选区 ----------

    private void OnHandleMouseDown(object sender, WinIn.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag })
        {
            _resizeHandle = tag;
            // 固定对角/对边锚点
            _anchor = tag switch
            {
                "NW" => new Point(_selection.Right, _selection.Bottom),
                "NE" => new Point(_selection.Left, _selection.Bottom),
                "SE" => new Point(_selection.Left, _selection.Top),
                "SW" => new Point(_selection.Right, _selection.Top),
                "N"  => new Point(_selection.Left, _selection.Bottom),
                "S"  => new Point(_selection.Left, _selection.Top),
                "W"  => new Point(_selection.Right, _selection.Top),
                "E"  => new Point(_selection.Left, _selection.Top),
                _ => _selection.TopLeft,
            };
            ((FrameworkElement)sender).CaptureMouse();
            e.Handled = true;
        }
    }

    private void OnHandleMouseMove(object sender, WinIn.MouseEventArgs e)
    {
        if (_resizeHandle != null)
        {
            ApplyResize(e.GetPosition(RootCanvas));
            e.Handled = true;
        }
    }

    private void OnHandleMouseUp(object sender, WinIn.MouseButtonEventArgs e)
    {
        if (_resizeHandle != null)
        {
            _resizeHandle = null;
            ((FrameworkElement)sender).ReleaseMouseCapture();
            LayoutSelectedChrome();
            e.Handled = true;
        }
    }

    private void ApplyResize(Point p)
    {
        double l = _selection.Left, t = _selection.Top;
        double r = _selection.Right, b = _selection.Bottom;

        if (_resizeHandle!.Contains('W')) l = Math.Min(p.X, _anchor.X - 4);
        if (_resizeHandle.Contains('E')) r = Math.Max(p.X, _anchor.X + 4);
        if (_resizeHandle.Contains('N')) t = Math.Min(p.Y, _anchor.Y - 4);
        if (_resizeHandle.Contains('S')) b = Math.Max(p.Y, _anchor.Y + 4);

        UpdateSelection(new Point(l, t), new Point(r, b));
    }

    // ---------- 键盘 ----------

    private void OnKeyDown(object sender, WinIn.KeyEventArgs e)
    {
        switch (e.Key)
        {
            case WinIn.Key.Escape:
                Cancel();
                e.Handled = true;
                break;
            case WinIn.Key.Enter when _hasSelection:
                Confirm();
                e.Handled = true;
                break;
            case WinIn.Key.C when WinIn.Keyboard.Modifiers.HasFlag(WinIn.ModifierKeys.Control) && _hasSelection:
                Confirm(copyImage: true);
                e.Handled = true;
                break;
        }
    }

    private void OnTranslateClick(object sender, RoutedEventArgs e) => Confirm();

    private void OnCopyClick(object sender, RoutedEventArgs e) => Confirm(copyImage: true);

    private void OnCancelClick(object sender, RoutedEventArgs e) => Cancel();

    // ---------- 确认 / 取消 ----------

    private void Confirm(bool copyImage = false)
    {
        if (!_hasSelection || _frozen == null || RootCanvas.ActualWidth <= 0) return;

        // DIP → 物理像素（比例映射，DPI 无关）
        var physical = new Rectangle(
            (int)Math.Round(_selection.X * RatioX),
            (int)Math.Round(_selection.Y * RatioY),
            (int)Math.Round(_selection.Width * RatioX),
            (int)Math.Round(_selection.Height * RatioY));

        if (copyImage)
        {
            try
            {
                using var cropped = ScreenCaptureService.Crop(_frozen, physical);
                ScreenCaptureService.CopyToClipboard(cropped);
            }
            catch { /* 复制失败不阻断 */ }
        }

        var handler = Confirmed;
        CleanupAndClose();
        handler?.Invoke(physical);
    }

    private void Cancel()
    {
        var handler = Cancelled;
        CleanupAndClose();
        handler?.Invoke();
    }

    private void CleanupAndClose()
    {
        _frozen = null;
        FrozenImage.Source = null;
        Close();
    }

    // ---------- 渲染 ----------

    private void UpdateSelection(Point a, Point b)
    {
        _selection = new Rect(
            Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
            Math.Max(Math.Abs(b.X - a.X), 1), Math.Max(Math.Abs(b.Y - a.Y), 1));

        LayoutSelection();
    }

    private void LayoutSelection()
    {
        // 空选区保护：Rect.Empty.X 为正无穷大，直接设置 Canvas.Left 会抛
        // "'∞' 不是属性 'Left' 的有效值"（OnLoaded / 误触拖拽路径会走到这里）
        if (_selection.IsEmpty || double.IsInfinity(_selection.X) || double.IsNaN(_selection.X)
            || double.IsInfinity(_selection.Y) || double.IsNaN(_selection.Y))
        {
            SelectionBorder.Visibility = Visibility.Collapsed;
            MaskPath.Visibility = Visibility.Collapsed;
            SizeTip.Visibility = Visibility.Collapsed;
            return;
        }

        Canvas.SetLeft(SelectionBorder, _selection.X);
        Canvas.SetTop(SelectionBorder, _selection.Y);
        SelectionBorder.Width = _selection.Width;
        SelectionBorder.Height = _selection.Height;
        SelectionBorder.Visibility = _hasSelection ? Visibility.Visible : Visibility.Collapsed;

        // 遮罩挖洞：外框 = 窗口全尺寸，内框 = 选区，EvenOdd
        var outer = new WinMedia.RectangleGeometry(new Rect(0, 0, RootCanvas.ActualWidth, RootCanvas.ActualHeight));
        var inner = new WinMedia.RectangleGeometry(_selection);
        var group = new WinMedia.GeometryGroup { FillRule = WinMedia.FillRule.EvenOdd };
        group.Children.Add(outer);
        group.Children.Add(inner);
        MaskPath.Data = group;
        MaskPath.Visibility = _hasSelection ? Visibility.Visible : Visibility.Collapsed;

        // 尺寸提示（显示物理像素，直观）
        if (_hasSelection && _frozen != null && RootCanvas.ActualWidth > 0)
        {
            SizeTipText.Text = $"{(int)Math.Round(_selection.Width * RatioX)} × {(int)Math.Round(_selection.Height * RatioY)}";
            Canvas.SetLeft(SizeTip, _selection.X);
            Canvas.SetTop(SizeTip, _selection.Y - 26 < 0 ? _selection.Bottom + 4 : _selection.Y - 26);
            SizeTip.Visibility = Visibility.Visible;
        }
        else
        {
            SizeTip.Visibility = Visibility.Collapsed;
        }

        if (!_dragging && _hasSelection) LayoutSelectedChrome();
    }

    private void EnterSelectedState()
    {
        LayoutSelectedChrome();
    }

    private void LayoutSelectedChrome()
    {
        // 松手即确认模式：无编辑态，手柄/工具条永久隐藏（保留元素仅为兼容现有代码路径）
        foreach (var el in new FrameworkElement[] { HandleNW, HandleN, HandleNE, HandleE, HandleSE, HandleS, HandleSW, HandleW, Toolbar })
        {
            el.Visibility = Visibility.Collapsed;
        }
    }

    private void ResetSelection()
    {
        _hasSelection = false;
        _dragging = false;
        _resizeHandle = null;
        _selection = Rect.Empty;
        LayoutSelection();
        LayoutSelectedChrome();
    }

    // ---------- Win32 定位常量 ----------
    private static readonly nint HWND_TOPMOST = -1;
    private const uint SWP_FRAMECHANGED = 0x0020;
}
