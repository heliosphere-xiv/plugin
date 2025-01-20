using System.Net.Http.Headers;
using Microsoft.AspNetCore.WebUtilities;

namespace Heliosphere.Util;

internal interface IMultipartProvider : IDisposable, IAsyncDisposable {
    Task<Stream?> GetNextStreamAsync(CancellationToken token = default);
}

internal class StandardMultipartProvider : IMultipartProvider {
    private string Boundary { get; }
    private HttpContent Content { get; }
    private Stream? ContentStream { get; set; }
    private MultipartReader? Reader { get; set; }


    internal StandardMultipartProvider(string boundary, HttpContent content) {
        this.Boundary = boundary;
        this.Content = content;
    }

    public void Dispose() {
        this.ContentStream?.Dispose();
        this.Content.Dispose();
    }

    public ValueTask DisposeAsync() {
        if (this.ContentStream is { } stream) {
            return stream.DisposeAsync();
        }

        this.Content.Dispose();
        return ValueTask.CompletedTask;
    }

    public async Task<Stream?> GetNextStreamAsync(CancellationToken token = default) {
        if (this.Reader == null) {
            this.ContentStream ??= await this.Content.ReadAsStreamAsync(token);
            this.Reader = new MultipartReader(this.Boundary, this.ContentStream);
        }

        var section = await this.Reader.ReadNextSectionAsync(token);
        return section?.Body;
    }
}

internal class SingleMultipartProvider : IMultipartProvider {
    private HttpContent Content { get; }

    internal SingleMultipartProvider(HttpContent content) {
        this.Content = content;
    }

    public async Task<Stream?> GetNextStreamAsync(CancellationToken token = default) {
        return await this.Content.ReadAsStreamAsync(token);
    }

    public void Dispose() {
        this.Content.Dispose();
    }

    public ValueTask DisposeAsync() {
        this.Content.Dispose();
        return ValueTask.CompletedTask;
    }
}

internal class SingleMultipleMultipartProvider : IMultipartProvider {
    private HttpContent Content { get; }
    private ICollection<RangeItemHeaderValue> Ranges { get; }
    private WorkaroundStream? Stream { get; set; }

    internal SingleMultipleMultipartProvider(HttpContent content, ICollection<RangeItemHeaderValue> ranges) {
        this.Content = content;
        this.Ranges = ranges;
    }

    public async Task<Stream?> GetNextStreamAsync(CancellationToken token = default) {
        if (this.Stream is {} cached) {
            return cached;
        }

        var stream = await this.Content.ReadAsStreamAsync(token);
        this.Stream = new WorkaroundStream(stream, this.Ranges);
        return this.Stream;
    }

    public void Dispose() {
        this.Content.Dispose();
    }

    public ValueTask DisposeAsync() {
        this.Content.Dispose();
        return ValueTask.CompletedTask;
    }

    internal class WorkaroundStream : Stream {
        private Stream Inner { get; }
        private IList<RangeItemHeaderValue> Ranges { get; }
        private int _rangeIdx;
        private int _readInRange;

        private RangeItemHeaderValue CurrentRange => this.Ranges[this._rangeIdx];

        internal WorkaroundStream(Stream inner, ICollection<RangeItemHeaderValue> ranges) {
            this.Inner = inner;
            this.Ranges = [ .. ranges.OrderBy(range => range.From) ];
        }

        public override bool CanRead => this.Inner.CanRead;

        public override bool CanSeek => this.Inner.CanSeek;

        public override bool CanWrite => this.Inner.CanWrite;

        public override long Length => this.Inner.Length;

        public override long Position {
            get => this.Inner.Position;
            set => this.Inner.Position = value;
        }

        public override void Flush() {
            this.Inner.Flush();
        }

        public override int Read(byte[] buffer, int offset, int count) {
            // first, calculate how much is left over in this range
            var range = this.CurrentRange;
            var totalRangeSize = range.To - range.From;
            var leftOver = totalRangeSize - this._readInRange;
            if (leftOver == null) {
                throw new NullReferenceException("unexpected null bound of range header");
            }

            // next, clamp the amount of data we're reading if necessary
            var actualLimit = Math.Min((int) leftOver, count);

            // perform the read and take note of the amount of bytes read
            var amountRead = this.Inner.Read(buffer, offset, actualLimit);

            // keep track of the total amount of bytes read in this range.
            this._readInRange += amountRead;

            // if we've read an amount equal to or greater than the range's
            // total size, we need to handle switching between ranges.
            if (this._readInRange >= totalRangeSize) {
                // this is an unacceptable error (can corrupt files)
                if (this._readInRange > totalRangeSize) {
                    throw new Exception("read too many bytes from range in workaround");
                }

                // if there's a range after this, perform logic for switching to
                // it.
                if (this._rangeIdx + 1 < this.Ranges.Count) {
                    var nextRange = this.Ranges[this._rangeIdx + 1];
                    var bytesBetween = nextRange.From - range.To;
                    if (bytesBetween == null) {
                        throw new Exception("unexpected null bound of range header (2)");
                    }

                    var scratch = new byte[81_920];
                    // don't dispose this, since that would dispose the inner
                    // stream!
                    new LimitedStream(this.Inner, (int) bytesBetween).ReadToEnd(scratch);
                }

                this._rangeIdx += 1;
                this._readInRange = 0;
            }

            return amountRead;
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
}
