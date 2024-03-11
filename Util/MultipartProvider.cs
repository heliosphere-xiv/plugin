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
        if (section == null) {
            return null;
        }

        return section.Body;
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
