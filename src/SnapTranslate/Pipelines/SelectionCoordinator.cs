using System.Drawing;
using SW = System.Windows;
using SWMedia = System.Windows.Media;
using System.Windows.Media.Imaging;
using SnapTranslate.Models;
using SnapTranslate.Services;
using SnapTranslate.Views;

namespace SnapTranslate.Pipelines;

/// <summary>
/// 主链路编排（设计文档 §3.1）：热键 → 抓屏 → 框选 → 裁剪 → OCR → 翻译 → 钉屏浮窗。
/// </summary>
internal sealed class SelectionCoordinator : IDisposable
{
    private readonly HotkeyService _hotkey;
    private readonly ScreenCaptureService _capture;
    private readonly OverlayManager _overlay;
    private readonly OcrService _ocr;
    private readonly TranslationRouter _translator;
    private readonly ConfigService _config;
    private readonly SemaphoreSlim _gate = new(1, 1); // 重入保护：截图进行中忽略热键

    public SelectionCoordinator(
        HotkeyService hotkey,
        ScreenCaptureService capture,
        OverlayManager overlay,
        OcrService ocr,
        TranslationRouter translator,
        ConfigService config)
    {
        _hotkey = hotkey;
        _capture = capture;
        _overlay = overlay;
        _ocr = ocr;
        _translator = translator;
        _config = config;

        _hotkey.HotkeyPressed += () => _ = TriggerCaptureAsync();
        // 热键注册由 App.OnStartup 统一处理（读取用户配置，含冲突回退），此处不再注册
    }

    public async Task TriggerCaptureAsync()
    {
        var app = SW.Application.Current;
        if (app is null) return;

        if (!await _gate.WaitAsync(0)) return; // 上一次流程未结束
        try
        {
            // 0) "新一轮截图自动关闭旧浮窗"（用户设置，防浮窗堆屏）
            if (_config.GetClosePreviousPin())
            {
                app.Dispatcher.Invoke(PinWindowManager.CloseAll);
            }

            // 1) 先冻结屏幕（必须在显示遮罩之前，设计文档 §4.2）
            Bitmap? frozen = null;
            var bounds = default(Rectangle);
            await app.Dispatcher.InvokeAsync(() =>
            {
                frozen = _capture.CaptureVirtualScreen(out bounds);
            });
            if (frozen is null) return;

            // 2) 显示遮罩，等待选区
            var selection = await _overlay.SelectRegionAsync(frozen, bounds);
            if (selection is null)
            {
                frozen.Dispose();
                return; // Esc 取消
            }

            // 3) 裁剪选区
            Bitmap? cropped = null;
            await app.Dispatcher.InvokeAsync(() =>
                cropped = ScreenCaptureService.Crop(frozen, selection.Value));
            frozen.Dispose();
            if (cropped is null) return;

            // 4) 钉屏浮窗立即出现（加载中无占位文本），后续结果原地刷新
            var pin = app.Dispatcher.Invoke(() =>
            {
                var w = new PinResultWindow();
                w.RetryRequested += () => _ = RunTranslateAsync(w, cropped);
                // "再截取"：直接发起新一轮截图翻译（用户需求：浮窗内一键再截取）
                w.RecaptureRequested += () => _ = TriggerCaptureAsync();
                w.ShowAt(DipSelectionRect(selection.Value));
                PinWindowManager.Track(w);
                return w;
            });

            // 5) OCR → 翻译（后台线程，浮窗已显示不阻塞交互）
            await RunTranslateAsync(pin, cropped);
        }
        catch (Exception ex)
        {
            Logger.Error("截图流程出错", ex);
            SW.MessageBox.Show(
                $"截图流程出错：{ex.GetType().Name}: {ex.Message}\n\n完整详情见日志：\n%APPDATA%\\SnapTranslate\\app.log",
                "SnapTranslate",
                SW.MessageBoxButton.OK, SW.MessageBoxImage.Warning);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RunTranslateAsync(PinResultWindow pin, Bitmap cropped)
    {
        try
        {
            // OCR（离线，毫秒级）
            var outcome = await _ocr.RecognizeAsync(cropped);
            if (outcome is null)
            {
                pin.SetOcrUnavailable();
                return;
            }
            if (string.IsNullOrEmpty(outcome.FullText))
            {
                pin.SetOcrEmpty();
                return;
            }
            pin.SetOcrResult(outcome);

            // 翻译（联网；引擎按配置路由：百度已配置用百度，否则 MyMemory 免注册引擎）
            var result = await _translator.TranslateAsync(outcome.FullText, "en", "zh");
            pin.SetTranslation(result);
        }
        catch (Exception ex)
        {
            Logger.Error("翻译链路出错", ex);
            pin.SetTranslation(TranslationResult.Fail("PIPELINE", ex.Message));
        }
    }

    /// <summary>物理像素选区 → DIP 矩形（浮窗定位用，近似换算即可）</summary>
    private static SW.Rect DipSelectionRect(Rectangle physical)
    {
        // 浮窗初始定位允许小误差：取屏幕 DC 的 DPI
        double dipPerPx = 1.0;
        try
        {
            using var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero);
            dipPerPx = 96.0 / g.DpiX;   // DIP = 物理像素 × (96/DPI)
        }
        catch { /* 取不到就按 100% */ }

        return new SW.Rect(
            physical.X * dipPerPx,
            physical.Y * dipPerPx,
            physical.Width * dipPerPx,
            physical.Height * dipPerPx);
    }

    public void Dispose() => _hotkey.Dispose();
}

/// <summary>
/// 遮罩生命周期管理：同一时间只允许一个 OverlayWindow。
/// </summary>
internal sealed class OverlayManager
{
    private OverlayWindow? _current;

    /// <summary>显示遮罩并等待用户框选；返回 null 表示取消</summary>
    public Task<Rectangle?> SelectRegionAsync(Bitmap frozen, Rectangle virtualBounds)
    {
        var app = SW.Application.Current ?? throw new InvalidOperationException("Application 未启动");

        var tcs = new TaskCompletionSource<Rectangle?>(TaskCreationOptions.RunContinuationsAsynchronously);

        app.Dispatcher.Invoke(() =>
        {
            if (_current != null) { tcs.TrySetResult(null); return; }

            var overlay = new OverlayWindow();
            _current = overlay;

            overlay.Confirmed += rect =>
            {
                _current = null;
                tcs.TrySetResult(rect);
            };
            overlay.Cancelled += () =>
            {
                _current = null;
                tcs.TrySetResult(null);
            };

            overlay.ShowOverlay(frozen, virtualBounds);
        });

        return tcs.Task;
    }
}
