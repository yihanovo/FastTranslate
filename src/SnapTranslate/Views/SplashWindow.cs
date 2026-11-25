using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace SnapTranslate.Views;

/// <summary>
/// 启动欢迎卡片：双击 exe 后给用户即时视觉反馈（"我真的启动了"）。
/// 屏幕中央淡入 → 停留 1.2s → 淡出；不抢焦点、不进任务栏。
/// </summary>
internal sealed class SplashWindow : Window
{
    public SplashWindow(string hotkeyDisplay)
    {
        // 窗口形态：无边框透明、置顶、不激活
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        Topmost = true;
        ShowActivated = false;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = 300;
        Height = 104;
        Opacity = 0;

        // 内容：圆角卡片 = 图标 + 标题 + 热键提示
        var card = new Border
        {
            Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(246, 255, 255, 255)),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(22, 16, 22, 16),
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(40, 208, 215, 222)),
            BorderThickness = new Thickness(1),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 18, ShadowDepth = 2, Opacity = 0.30,
            },
        };

        var icon = new System.Windows.Controls.Image
        {
            Source = new BitmapImage(new Uri("pack://application:,,,/Assets/tray.ico")),
            Width = 36, Height = 36, VerticalAlignment = VerticalAlignment.Center,
        };

        var title = new System.Windows.Controls.TextBlock
        {
            Text = "SnapTranslate 已启动",
            FontSize = 15, FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1F, 0x23, 0x28)),
        };
        var hint = new System.Windows.Controls.TextBlock
        {
            Text = $"按 {hotkeyDisplay} 开始截图翻译",
            FontSize = 12, Margin = new Thickness(0, 3, 0, 0),
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2D, 0x7D, 0xD2)),
        };

        var textPanel = new System.Windows.Controls.StackPanel { VerticalAlignment = VerticalAlignment.Center };
        textPanel.Children.Add(title);
        textPanel.Children.Add(hint);

        var row = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        row.Children.Add(icon);
        row.Children.Add(new System.Windows.Controls.Border
        {
            Width = 14, // 图标与文字间距
        });
        row.Children.Add(textPanel);
        card.Child = row;
        Content = card;

        // 居中（工作区中央略偏上，视觉更稳）
        Loaded += (_, _) =>
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Left + (wa.Width - Width) / 2;
            Top = wa.Top + wa.Height * 0.32;

            // 淡入 200ms → 停留 1.2s → 淡出 350ms → 关闭
            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            };
            fadeIn.Completed += (_, _) =>
            {
                var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(350))
                {
                    BeginTime = TimeSpan.FromMilliseconds(1200),
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
                };
                fadeOut.Completed += (_, _) => Dispatcher.Invoke(Close);
                BeginAnimation(OpacityProperty, fadeOut);
            };
            BeginAnimation(OpacityProperty, fadeIn);
        };
    }
}
