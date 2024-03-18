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
    internal SentrySpan StartChild(string operation, bool independent = false) {
        var child = this.LatestChild();

        ISpan span;
        Action<SentrySpan> setChild;
        Action becomeDisowned;
        if (child != null) {
            span = child.Inner.StartChild(operation);
            becomeDisowned = () => child.Child = null;
            setChild = wrapped => child.Child = wrapped;
        } else {
            span = this.Inner.StartChild(operation);
            becomeDisowned = () => this.Child = null;
            setChild = wrapped => this.Child = wrapped;
        }

        var wrapped = new SentrySpan(span, becomeDisowned);

        if (!independent) {
            setChild(wrapped);
        }

        return wrapped;
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
