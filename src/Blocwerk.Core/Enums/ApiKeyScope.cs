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

    /// <summary>
    /// A machine acting for the WHOLE installation rather than for one wall or one person: the
    /// autodeploy hook announcing that it is about to recreate the container. Carries no
    /// <c>WallId</c>, and its <c>UserId</c> is the admin who minted it — recorded so the key can
    /// always be traced back to a person, never so that it inherits that person's access.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT part of <c>BlocwerkPolicies.AnyApiKey</c>, exactly as <see cref="Kiosk"/>
    /// is not: "any key" means the ordinary wall/user API surface, and an installation key must
    /// only ever satisfy the one policy written for it. It is minted by an app administrator and
    /// by nobody else.
    /// </remarks>
    Installation = 3,
}
