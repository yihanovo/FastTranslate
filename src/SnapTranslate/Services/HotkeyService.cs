using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using SnapTranslate.Interop;

namespace SnapTranslate.Services;

/// <summary>
/// 全局热键服务（设计文档 §4.1）。
/// 通过隐藏消息窗口接收 WM_HOTKEY，不依赖任何可见窗口。
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private HwndSource? _source;
    private bool _registered;

    /// <summary>热键触发（在 UI 线程回调）</summary>
    public event Action? HotkeyPressed;

    private Key _key = Key.F1;
    private ModifierKeys _modifiers = ModifierKeys.None;

    public HotkeyService()
    {
        // 构造即创建消息窗口：注册失败/未注册状态下设置界面也能换绑热键
        EnsureSource();
    }

    private void EnsureSource()
    {
        if (_source != null) return;

        // 必须在 UI 线程创建（HwndSource 需要 Dispatcher）
        var dispatcher = System.Windows.Application.Current?.Dispatcher
            ?? System.Windows.Threading.Dispatcher.FromThread(System.Threading.Thread.CurrentThread);
        if (dispatcher is null) throw new InvalidOperationException("无可用 Dispatcher，无法创建热键消息窗口");

        dispatcher.Invoke(() =>
        {
            if (_source != null) return;

            var parameters = new HwndSourceParameters("SnapTranslateHotkeyHost")
            {
                // 零尺寸窗口：纯消息宿主，不可见、不出现在任务栏/Alt-Tab
                ParentWindow = IntPtr.Zero,
                Width = 0,
                Height = 0,
                WindowStyle = unchecked((int)0x80000000), // WS_POPUP
            };
            _source = new HwndSource(parameters);
            _source.AddHook(WndProc);
        });
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == NativeMethods.HOTKEY_ID)
        {
            // Dispatcher 异步触发：避免阻塞消息循环（设计文档 §4.1）
            _source?.Dispatcher.BeginInvoke(DispatcherPriority.Input, () => HotkeyPressed?.Invoke());
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Register(Key key, ModifierKeys modifiers)
    {
        if (_source == null) throw new InvalidOperationException("消息窗口未初始化");

        Unregister();

        uint mods = modifiers switch
        {
            ModifierKeys.None => 0u,
            _ => (uint)modifiers,
        };

        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (!NativeMethods.RegisterHotKey(_source.Handle, NativeMethods.HOTKEY_ID, mods, vk))
        {
            throw new InvalidOperationException($"热键 {modifiers}+{key} 注册失败，可能已被其他程序占用");
        }

        _key = key;
        _modifiers = modifiers;
        _registered = true;
        IsEnabled = true;
    }

    public void Unregister()
    {
        if (!_registered || _source == null) return;
        NativeMethods.UnregisterHotKey(_source.Handle, NativeMethods.HOTKEY_ID);
        _registered = false;
    }

    /// <summary>禁用热键：注销系统注册但保留绑定配置（供设置界面切换）</summary>
    public void Disable()
    {
        Unregister();
        IsEnabled = false;
    }

    /// <summary>启用热键：按当前绑定重新注册；失败抛 InvalidOperationException</summary>
    public void Enable()
    {
        Register(_key, _modifiers);
        IsEnabled = true;
    }

    /// <summary>热键当前是否生效（禁用 = 让出全局快捷键给其他应用）</summary>
    public bool IsEnabled { get; private set; }

    public void Dispose()
    {
        Unregister();
        _source?.RemoveHook(WndProc);
        _source?.Dispose();
        _source = null;
    }
}
