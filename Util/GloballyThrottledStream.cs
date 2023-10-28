using System.Diagnostics;
using DequeNet;

namespace Heliosphere.Util;

internal class GloballyThrottledStream : Stream {
    private static long _maxBytesPerSecond;

    internal static long MaxBytesPerSecond {
        get => _maxBytesPerSecond;
        set {
            // if new value is bigger, this is positive
            var diff = value - _maxBytesPerSecond;
            _maxBytesPerSecond = value;

            Mutex.Wait();
            try {
                _freqTokens += diff * Stopwatch.Frequency;
            } finally {
                Mutex.Release();
            }
        }
    }

    private static readonly SemaphoreSlim Mutex = new(1, 1);
    private static long _freqTokens;
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

    private static long AddTokens() {
        var now = Stopwatch.GetTimestamp();
        var then = Interlocked.Exchange(ref _lastRead, now);
        var tokensToAdd = (now - then) * _maxBytesPerSecond;

        long freqTokens;
        Mutex.Wait();
        try {
            var untilFull = _maxBytesPerSecond * Stopwatch.Frequency - _freqTokens;
            if (untilFull > 0 && tokensToAdd > 0) {
                _freqTokens += Math.Min(untilFull, tokensToAdd);
            }

            freqTokens = _freqTokens;
        } finally {
            Mutex.Release();
        }

        return freqTokens;

        // var now = Stopwatch.GetTimestamp();
        // var then = Interlocked.Exchange(ref _lastRead, now);
        // var tokensToAdd = (now - then) * _maxBytesPerSecond;
        //
        // var curTokens = Interlocked.CompareExchange(ref _freqTokens, 0, 0);
        // var untilFull = _maxBytesPerSecond * Stopwatch.Frequency - curTokens;
        // var freqTokens = untilFull > 0 && tokensToAdd > 0
        //     ? Interlocked.Add(ref _freqTokens, Math.Min(untilFull, tokensToAdd))
        //     : curTokens;
        //
        // return freqTokens;
    }

    public override int Read(byte[] buffer, int offset, int count) {
        var freqTokens = AddTokens();

        int amt;
        if (_maxBytesPerSecond == 0) {
            amt = count;
        } else {
            var bytes = (int) (freqTokens / Stopwatch.Frequency);
            // let's not do a million tiny reads
            var exp = (int) Math.Truncate(Math.Log2(_maxBytesPerSecond));
            var lessThan = Math.Pow(2, Math.Clamp(exp, 0, 16));
            while (bytes < lessThan) {
                Thread.Sleep(TimeSpan.FromMilliseconds(5));
                freqTokens = AddTokens();
                bytes = (int) (freqTokens / Stopwatch.Frequency);
            }

            amt = Math.Min(bytes, count);
        }

        var read = this.Inner.Read(buffer, offset, amt);

        Mutex.Wait();
        try {
            _freqTokens -= read * Stopwatch.Frequency;
        } finally {
            Mutex.Release();
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
