using System.Diagnostics;
using DequeNet;

namespace Heliosphere.Util;

internal class GloballyThrottledStream : Stream {
    private static long _maxBytesPerSecond;

    internal static long MaxBytesPerSecond {
        get => _maxBytesPerSecond;
        set => Interlocked.Exchange(ref _maxBytesPerSecond, value);
    }

    private static readonly SemaphoreSlim Mutex = new(1, 1);

    /// <summary>
    /// The fill level of the leaky bucket. Number of bytes read multiplied by
    /// <see cref="Stopwatch.Frequency"/>.
    /// </summary>
    private static long _bucket;

    private static long _lastRead = Stopwatch.GetTimestamp();

    private Stream Inner { get; }
    private ConcurrentDeque<DownloadTask.Measurement> Entries { get; }

    public override bool CanRead => this.Inner.CanRead;
    public override bool CanSeek => this.Inner.CanSeek;
    public override bool CanWrite => this.Inner.CanWrite;
    public override long Length => this.Inner.Length;

    public override long Position {
        get => this.Inner.Position;
        set => this.Inner.Position = value;
    }

    internal GloballyThrottledStream(Stream inner, ConcurrentDeque<DownloadTask.Measurement> entries) {
        this.Inner = inner;
        this.Entries = entries;
    }

    protected override void Dispose(bool disposing) {
        if (disposing) {
            this.Inner.Dispose();
        }
    }

    public override ValueTask DisposeAsync() {
        return this.Inner.DisposeAsync();
    }

    internal static void Shutdown() {
        Mutex.Dispose();
    }

    public override void Flush() {
        this.Inner.Flush();
    }

    private static long Leak(long mbps) {
        var now = Stopwatch.GetTimestamp();
        var then = Interlocked.Exchange(ref _lastRead, now);
        var leakAmt = (now - then) * mbps;

        long bucket;
        Mutex.Wait();
        try {
            if (_bucket > 0) {
                _bucket = Math.Max(0, _bucket - leakAmt);
            }

            bucket = mbps * Stopwatch.Frequency - _bucket;
        } finally {
            Mutex.Release();
        }

        return bucket;
    }

    public override int Read(byte[] buffer, int offset, int count) {
        var mbps = _maxBytesPerSecond;

        int amt;
        if (mbps == 0) {
            amt = count;
        } else {
            // available capacity in the bucket * freq
            var bucket = Leak(mbps);
            // number of bytes the bucket has space for
            var bytes = (int) (bucket / Stopwatch.Frequency);

            // let's not do a million tiny reads
            // wait until between 1 and 65536 bytes are available, depending on
            // the buffer size and the speed limit
            var exp = (int) Math.Truncate(Math.Log2(Math.Min(count, mbps)));
            var lessThan = Math.Pow(2, Math.Clamp(exp, 0, 16));

            while (bytes < lessThan) {
                Thread.Sleep(TimeSpan.FromMilliseconds(5));
                bucket = Leak(mbps);
                bytes = (int) (bucket / Stopwatch.Frequency);
            }

            // read how many bytes are available or the buffer size, whichever
            // is smaller
            amt = Math.Min(bytes, count);
        }

        var read = this.Inner.Read(buffer, offset, amt);

        if (mbps != 0) {
            Mutex.Wait();
            try {
                _bucket += read * Stopwatch.Frequency;
            } finally {
                Mutex.Release();
            }
        }

        this.Entries.PushRight(new DownloadTask.Measurement {
            Ticks = Stopwatch.GetTimestamp(),
            Data = (uint) read,
        });

        return read;
    }

    public override long Seek(long offset, SeekOrigin origin) {
        return this.Inner.Seek(offset, origin);
    }

    public override void SetLength(long value) {
        this.Inner.SetLength(value);
    }

    public override void Write(byte[] buffer, int offset, int count) {
        this.Inner.Write(buffer, offset, count);
    }
}
