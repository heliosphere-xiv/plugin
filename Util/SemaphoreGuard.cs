namespace Heliosphere.Util;

internal class SemaphoreGuard : IDisposable {
    private SemaphoreSlim Semaphore { get; }

    private bool _disposed;

    private SemaphoreGuard(SemaphoreSlim semaphore) {
        this.Semaphore = semaphore;
    }

    internal static SemaphoreGuard Wait(SemaphoreSlim semaphore) {
        semaphore.Wait();
        return new SemaphoreGuard(semaphore);
    }

    internal static async Task<SemaphoreGuard> WaitAsync(SemaphoreSlim semaphore, CancellationToken token = default) {
        await semaphore.WaitAsync(token);
        return new SemaphoreGuard(semaphore);
    }

    public void Dispose() {
        if (this._disposed) {
            return;
        }

        this._disposed = true;

        this.Semaphore.Release();
    }
}
