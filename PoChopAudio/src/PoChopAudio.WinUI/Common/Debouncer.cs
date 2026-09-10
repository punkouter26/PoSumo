namespace PoChopAudio.WinUI.Common;

public sealed class Debouncer(TimeSpan delay) : IDisposable
{
    private CancellationTokenSource? _cts;

    public void Debounce(Func<Task> action)
    {
        _cts?.Cancel();
        _cts?.Dispose();

        var cts = new CancellationTokenSource();
        _cts = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, cts.Token);
                if (!cts.IsCancellationRequested)
                {
                    await action();
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when a newer invocation arrives.
            }
        });
    }

    public void Cancel()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose()
    {
        Cancel();
    }
}

