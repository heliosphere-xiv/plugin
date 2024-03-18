namespace Heliosphere.Util;

internal static class SentryHelper {
    internal static SentryTransaction StartTransaction(string name, string operation) {
        var transaction = SentrySdk.StartTransaction(name, operation);
        return new SentryTransaction(transaction);
    }
}

internal class SentryTransaction(ITransactionTracer inner) : IDisposable {
    internal ITransactionTracer Inner { get; } = inner;
    internal SentrySpan? Child { get; set; }

    private bool _disposed;

    public void Dispose() {
        if (this._disposed) {
            return;
        }

        this._disposed = true;
        this.Inner.Finish();
    }

    /// <summary>
    /// Start a child span as low in the hierarchy as possible.
    /// </summary>
    /// <param name="operation">name of the span operation</param>
    /// <returns>a span that must be disposed</returns>
    internal SentrySpan StartChild(string operation) {
        var child = this.LatestChild();
        if (child != null) {
            var span = child.Inner.StartChild(operation);
            var wrapped = new SentrySpan(span, () => child.Child = null);
            child.Child = wrapped;
            return wrapped;
        } else {
            var span = this.Inner.StartChild(operation);
            var wrapped = new SentrySpan(span, () => this.Child = null);
            this.Child = wrapped;
            return wrapped;
        }
    }

    internal SentrySpan? LatestChild() {
        var child = this.Child;
        while (child != null) {
            if (child.Child == null) {
                return child;
            }

            child = child.Child;
        }

        return null;
    }
}

internal class SentrySpan(ISpan inner, Action becomeDisowned) : IDisposable {
    internal ISpan Inner { get; } = inner;
    internal SentrySpan? Child { get; set; }
    private Action BecomeDisowned { get; } = becomeDisowned;
    private bool _disposed;

    public void Dispose() {
        if (this._disposed) {
            return;
        }

        this._disposed = true;
        this.Inner.Finish();

        this.BecomeDisowned();
    }
}
