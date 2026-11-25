using System.Drawing;
using System.Drawing.Imaging;
using System.Windows;

namespace SnapTranslate.Services;

/// <summary>
/// 屏幕抓取（设计文档 §4.2）。
/// 关键点：先 CopyFromScreen 抓全屏，之后才允许显示遮罩——顺序不能反。
/// 坐标系：全部使用物理像素（虚拟屏坐标系）。
/// </summary>
internal sealed class ScreenCaptureService
{
    /// <summary>抓取整个虚拟屏（多显示器）为 32bpp 位图，坐标为物理像素</summary>
    public Bitmap CaptureVirtualScreen(out Rectangle virtualBounds)
    {
        // 必须用 GetSystemMetrics（物理像素）：SystemParameters.VirtualScreen* 是 DIP，
        // 在 125%/150% 缩放下 CopyFromScreen 会截错区域（设计文档 §4.2）
        int left = SnapTranslate.Interop.NativeMethods.GetSystemMetrics(
            SnapTranslate.Interop.NativeMethods.SM_XVIRTUALSCREEN);
        int top = SnapTranslate.Interop.NativeMethods.GetSystemMetrics(
            SnapTranslate.Interop.NativeMethods.SM_YVIRTUALSCREEN);
        int width = SnapTranslate.Interop.NativeMethods.GetSystemMetrics(
            SnapTranslate.Interop.NativeMethods.SM_CXVIRTUALSCREEN);
        int height = SnapTranslate.Interop.NativeMethods.GetSystemMetrics(
            SnapTranslate.Interop.NativeMethods.SM_CYVIRTUALSCREEN);
        virtualBounds = new Rectangle(left, top, width, height);

        // WPF 的 VirtualScreen* 在 PerMonitorV2 下已是物理像素
        var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(left, top, 0, 0, new System.Drawing.Size(width, height));
        }
        return bmp;
    }

    /// <summary>按物理像素矩形裁剪选区（矩形已归一化）</summary>
    public static Bitmap Crop(Bitmap source, Rectangle selection)
    {
        // 防御：夹紧到源图范围
        var clamped = Rectangle.Intersect(selection, new Rectangle(0, 0, source.Width, source.Height));
        if (clamped.Width <= 0 || clamped.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(selection), "选区不在屏幕范围内");

        var result = new Bitmap(clamped.Width, clamped.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(result))
        {
            g.DrawImage(source, new Rectangle(0, 0, clamped.Width, clamped.Height),
                clamped, GraphicsUnit.Pixel);
        }
        return result;
    }

    /// <summary>把位图复制到剪贴板（用于 Ctrl+C 确认复制，设计文档 §4.3）</summary>
    public static void CopyToClipboard(Bitmap bmp)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            try
            {
                System.Windows.Forms.Clipboard.SetImage(bmp);
            }
            catch (Exception)
            {
                // 剪贴板被占用（例如正在被其他进程锁定）——忽略，主流程不受影响
            }
        });
    }
}
