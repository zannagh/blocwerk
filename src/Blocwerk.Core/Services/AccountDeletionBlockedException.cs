namespace Blocwerk.Core.Services;

/// <summary>
/// Thrown when a deletion cannot go ahead without orphaning or destroying other people's data —
/// today, when the user solely owns a wall that has no other admin to take it over.
/// </summary>
public sealed class AccountDeletionBlockedException : InvalidOperationException
{
    public AccountDeletionBlockedException(IReadOnlyList<string> wallNames)
        : base($"Account deletion is blocked by solely-owned wall(s): {string.Join(", ", wallNames)}.")
    {
        WallNames = wallNames;
    }

    /// <summary>The walls standing in the way, by name, so the UI can say which ones.</summary>
    public IReadOnlyList<string> WallNames { get; }
}
