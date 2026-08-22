namespace Blocwerk.Core.Enums;

/// <summary>What an API key is allowed to act on.</summary>
public enum ApiKeyScope
{
    /// <summary>Scoped to a single wall; the key carries that wall's id and nothing else.</summary>
    Wall = 0,

    /// <summary>Scoped to the user who created it, standing in for that user's own access.</summary>
    User = 1,
}
