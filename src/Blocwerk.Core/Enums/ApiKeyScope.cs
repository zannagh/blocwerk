namespace Blocwerk.Core.Enums;

/// <summary>What an API key is allowed to act on.</summary>
public enum ApiKeyScope
{
    /// <summary>Scoped to a single wall; the key carries that wall's id and nothing else.</summary>
    Wall = 0,

    /// <summary>Scoped to the user who created it, standing in for that user's own access.</summary>
    User = 1,

    /// <summary>
    /// Identifies a wall-mounted kiosk tablet. Like <see cref="Wall"/> the key carries a wall id, but
    /// deliberately as its own scope: a <see cref="Wall"/> key is accepted on the wall's write endpoints
    /// (temperature, images, maintenance) and a tablet in a public gym must not inherit those.
    /// </summary>
    Kiosk = 2,
}
