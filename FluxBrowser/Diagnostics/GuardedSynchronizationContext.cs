namespace FluxBrowser.Diagnostics;

/// <summary>
/// Wraps the UI thread's context so an exception thrown from an <c>async void</c> method (which the runtime re-posts to the
/// context) is logged and reported instead of taking the whole process down with every open tab.
/// </summary>
internal sealed class GuardedSynchronizationContext(SynchronizationContext inner, Action<Exception> report) : SynchronizationContext
{
    public override void Post(SendOrPostCallback d, object? state) => inner.Post(s => Run(d, s), state);

    public override void Send(SendOrPostCallback d, object? state) => inner.Send(s => Run(d, s), state);

    public override SynchronizationContext CreateCopy() => new GuardedSynchronizationContext(inner.CreateCopy(), report);

    private void Run(SendOrPostCallback callback, object? state)
    {
        try { callback(state); }
        catch (Exception exception) when (exception is not (OutOfMemoryException or AccessViolationException or StackOverflowException))
        {
            report(exception);
        }
    }
}
