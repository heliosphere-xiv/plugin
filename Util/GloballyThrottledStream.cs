using System.Diagnostics;
using DequeNet;

namespace Heliosphere.Util;

internal class GloballyThrottledStream : Stream {
    private static ulong _maxBytesPerSecond;

    internal static ulong MaxBytesPerSecond {
        get => Interlocked.Read(ref _maxBytesPerSecond);
        set => Interlocked.Exchange(ref _maxBytesPerSecond, value);
    }

    private static readonly SemaphoreSlim Mutex = new(1, 1);

    /// <summary>
    /// The fill level of the leaky bucket. Number of bytes read multiplied by
    /// <see cref="Stopwatch.Frequency"/>.
    /// </summary>
    private static ulong _bucket;

    private static ulong _lastRead = (ulong) Stopwatch.GetTimestamp();

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

    private static bool Leak(ulong mbps, ulong wantedBytes) {
        Mutex.Wait();
        try {
            var now = (ulong) Stopwatch.GetTimestamp();
            var then = _lastRead;
            var leakAmt = (now - then) * mbps;
            _lastRead = now;

            if (_bucket > 0 && leakAmt > 0) {
                _bucket = leakAmt >= _bucket
                    ? 0
                    : _bucket - leakAmt;
            }

            var max = mbps * (ulong) Stopwatch.Frequency;
            if (_bucket > max) {
                // by changing the speed limit, we have now overfilled the
                // bucket. remove excess
                _bucket = max;
            }

            var wantedFreq = wantedBytes * (ulong) Stopwatch.Frequency;
            if (_bucket + wantedFreq > max) {
                return false;
            }

            _bucket += wantedFreq;
            return true;
        } finally {
            Mutex.Release();
        }
    }

    public override int Read(byte[] buffer, int offset, int count) {
        int read;
        if (MaxBytesPerSecond == 0) {
            goto Unlimited;
        }

        var (mbps, toRead) = CalculateToRead();
        while (!Leak(mbps, toRead)) {
            Thread.Sleep(TimeSpan.FromMilliseconds(50));
            (mbps, toRead) = CalculateToRead();
            // make sure to check if the max bytes per sec was set to 0
            if (mbps == 0) {
                goto Unlimited;
            }
        }

        read = this.Inner.Read(buffer, offset, (int) toRead);
        // Leak says that we read this amount, so if we didn't, give back what
        // we didn't read
        if ((ulong) read < toRead) {
            Mutex.Wait();
            try {
                var over = (toRead - (ulong) read) * (ulong) Stopwatch.Frequency;
                _bucket = over >= _bucket
                    ? 0
                    : _bucket - over;
            } finally {
                Mutex.Release();
            }
        }

        this.Entries.PushRight(new DownloadTask.Measurement {
            Ticks = Stopwatch.GetTimestamp(),
            Data = (uint) read,
        });

        return read;

        Unlimited:
        read = this.Inner.Read(buffer, offset, count);
        this.Entries.PushRight(new DownloadTask.Measurement {
            Ticks = Stopwatch.GetTimestamp(),
            Data = (uint) read,
        });

        return read;

        (ulong Mbps, ulong ToRead) CalculateToRead() {
            // let's not do a million tiny reads, ask for a decent amount
            // wait until between 1 and 65536 bytes are available, depending on
            // the buffer size and the speed limit
            var mbps = MaxBytesPerSecond;
            var exp = (int) Math.Truncate(Math.Log2(Math.Min((ulong) count, mbps)));
            var wanted = (ulong) Math.Pow(2, Math.Clamp(exp, 0, 16));
            return (mbps, Math.Min(wanted, (ulong) count));
        }
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
