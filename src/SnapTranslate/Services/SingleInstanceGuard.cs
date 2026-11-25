using System.Threading;

namespace SnapTranslate.Services;

/// <summary>Mutex 单实例守卫（设计文档 §4.9）</summary>
internal sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;
    public bool IsFirstInstance { get; }

    public SingleInstanceGuard(string name)
    {
        _mutex = new Mutex(true, name, out bool createdNew);
        IsFirstInstance = createdNew;
    }

    public void Dispose() => _mutex.Dispose();
}
