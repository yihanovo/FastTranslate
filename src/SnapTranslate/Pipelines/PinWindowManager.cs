using System.Collections.ObjectModel;
using System.Windows;

namespace SnapTranslate.Pipelines;

/// <summary>
/// 活跃翻译浮窗注册表：
/// - "新一轮截图时自动关闭旧浮窗"（用户设置 closePreviousPin）
/// - 浮窗数量上限保护（防极端场景刷屏）
/// </summary>
internal static class PinWindowManager
{
    private const int MaxWindows = 8;

    private static readonly ObservableCollection<Window> _windows = new();
    public static IReadOnlyList<Window> Windows => _windows;

    /// <summary>浮窗显示时登记；自动挂接 Closed 注销</summary>
    public static void Track(Window window)
    {
        _windows.Add(window);
        window.Closed += (_, _) => _windows.Remove(window);

        // 超出上限：关最老的
        while (_windows.Count > MaxWindows)
        {
            _windows[0].Close();
        }
    }

    /// <summary>关闭当前全部浮窗</summary>
    public static void CloseAll()
    {
        foreach (var w in _windows.ToList())   // ToList：防迭代中 Close 触发集合变更
        {
            try { w.Close(); } catch { /* 关闭中异常忽略 */ }
        }
    }
}
