namespace IFactory.Interaction.EasyClient;

// Serial drivers do not all honor cancellation; abort only an outstanding write.
internal static class SerialWriteOperation
{
    internal static async Task RunAsync(Func<Task> start, Action abort, CancellationToken token)
    {
        var gate = new object();
        Task? write = null;
        bool cancelled = false;
        using var registration = token.Register(() =>
        {
            lock (gate)
            {
                cancelled = true;
                if (write is { IsCompleted: false }) abort();
            }
        });
        lock (gate)
        {
            if (cancelled) throw new OperationCanceledException(token);
            token.ThrowIfCancellationRequested();
            write = start();
        }
        try { await write.ConfigureAwait(false); }
        catch (Exception) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
    }
}
