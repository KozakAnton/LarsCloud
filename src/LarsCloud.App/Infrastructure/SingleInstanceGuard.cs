namespace LarsCloud.Infrastructure;

public sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;
    public bool IsFirstInstance { get; }

    public SingleInstanceGuard()
    {
        _mutex = new Mutex(true, @"Local\LarsCloud.SingleInstance", out var createdNew);
        IsFirstInstance = createdNew;
    }

    public void Dispose()
    {
        if (IsFirstInstance)
        {
            try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
        }
        _mutex.Dispose();
    }
}
