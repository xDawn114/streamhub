namespace StreamHub.Installer;

internal sealed class InstallTransactionLock : IDisposable
{
    private readonly Mutex _mutex;
    private InstallTransactionLock(Mutex mutex) => _mutex = mutex;
    internal static InstallTransactionLock? TryAcquire(string name)
    {
        var mutex = new Mutex(false, name);
        try
        {
            bool acquired;
            try { acquired = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            if (acquired) return new(mutex);
            mutex.Dispose(); return null;
        }
        catch { mutex.Dispose(); throw; }
    }
    public void Dispose() { _mutex.ReleaseMutex(); _mutex.Dispose(); }
}
