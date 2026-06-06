using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Blocwerk.Authentication.Resources;
using Blocwerk.Core.Helpers;

namespace Blocwerk.Authentication.Providers;

public class RedirectUriProvider
{
    private ConcurrentDictionary<string, RedirectSettings> StateRedirectUris { get; } = new();

    public RedirectUriProvider()
    {
        RecurringTask.Create(
            () => StateRedirectUris.Clear(),
            TimeSpan.FromMinutes(10),
            CancellationToken.None);
    }

    public void AddRedirectUri(string state, RedirectSettings redirectSettings)
    {
        StateRedirectUris.TryAdd(state, redirectSettings);
    }

    public bool GetRedirectUri(string state, [MaybeNullWhen(false)] out RedirectSettings redirectSettings)
    {
        return StateRedirectUris.TryRemove(state, out redirectSettings);
    }
}
