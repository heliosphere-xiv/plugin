namespace Heliosphere.Util;

internal class Guard<T> : IDisposable {
    private T Data { get; }
    private SemaphoreSlim Mutex { get; } = new(1, 1);
    private bool _disposed;

    internal Guard(T data) {
        this.Data = data;
    }

    /// <summary>
    /// Dispose the internal <see cref="SemaphoreSlim"/> and return the data.
    /// This marks the instance as disposed, so <see cref="Guard{T}.Dispose"/>
    /// will do nothing.
    /// </summary>
    /// <returns>Previously-guarded data</returns>
    /// <exception cref="InvalidOperationException">If this instance is already disposed</exception>
    internal T Deconstruct() {
        if (this._disposed) {
            throw new InvalidOperationException("already disposed");
        }

        this._disposed = true;
        this.Mutex.Dispose();
        return this.Data;
    }

    public void Dispose() {
        if (this._disposed) {
            return;
        }

        this._disposed = true;
        this.Mutex.Dispose();
        if (this.Data is IDisposable disposable) {
            disposable.Dispose();
        }
    }

    internal Handle Wait() {
        this.Mutex.Wait();
        return new Handle(this.Data, this.Mutex);
    }

    internal Handle? Wait(int timeout) {
        return this.Mutex.Wait(timeout)
            ? new Handle(this.Data, this.Mutex)
            : null;
    }

    internal async Task<Handle> WaitAsync(CancellationToken token = default) {
        await this.Mutex.WaitAsync(token);
        return new Handle(this.Data, this.Mutex);
    }

    internal class Handle : IDisposable {
        internal T Data { get; }
        private SemaphoreSlim Mutex { get; }

        private bool _disposed;

        protected internal Handle(T data, SemaphoreSlim mutex) {
            this.Data = data;
            this.Mutex = mutex;
        }

        public void Dispose() {
            if (this._disposed) {
                return;
            }

            this._disposed = true;
            this.Mutex.Release();
        }
    }
}
