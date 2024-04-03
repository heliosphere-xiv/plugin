namespace Heliosphere.Util;

internal static class HttpClientExt {
    internal async static Task<HttpResponseMessage> GetAsync2(
        this HttpClient client,
        Uri requestUri,
        HttpCompletionOption completion,
        CancellationToken token = default
    ) {
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        return await client.SendAsync2(request, completion, token);
    }

    internal async static Task<HttpResponseMessage> GetAsync2(
        this HttpClient client,
        string requestUri,
        HttpCompletionOption completion,
        CancellationToken token = default
    ) {
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        return await client.SendAsync2(request, completion, token);
    }

    internal async static Task<HttpResponseMessage> SendAsync2(
        this HttpClient client,
        HttpRequestMessage request,
        HttpCompletionOption completion,
        CancellationToken token = default
    ) {
        try {
            return await client.SendAsync(request, completion, token);
        } catch (TaskCanceledException ex) {
            if (!token.IsCancellationRequested) {
                // HttpClient timed out
                throw new TimeoutException($"Request exceeded timeout ({client.Timeout})", ex);
            }

            throw;
        }
    }

    internal async static Task<byte[]> GetByteArrayAsync2(
        this HttpClient client,
        string uri,
        CancellationToken token = default
    ) {
        try {
            return await client.GetByteArrayAsync(uri, token);
        } catch (TaskCanceledException ex) {
            if (!token.IsCancellationRequested) {
                // HttpClient timed out
                throw new TimeoutException($"Request exceeded timeout ({client.Timeout})", ex);
            }

            throw;
        }
    }
}
