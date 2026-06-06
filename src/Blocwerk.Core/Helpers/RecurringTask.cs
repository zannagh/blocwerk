namespace Blocwerk.Core.Helpers;

public static class RecurringTask
{
    public static void Create(Action action, TimeSpan interval, CancellationToken cancellationToken)
    {
        _ = Task.Run(async () =>
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(interval, cancellationToken);
                action();
            }
        }, cancellationToken);
    }
}
