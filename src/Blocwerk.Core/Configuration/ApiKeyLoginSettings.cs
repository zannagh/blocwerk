namespace Blocwerk.Core.Configuration;

/// <summary>
/// Signing a browser session in with a personal API key (<c>POST /account/api-key-login</c>), for
/// automation such as Playwright driving a production-mode instance without an OAuth round-trip.
/// </summary>
/// <remarks>
/// Off by default, and while off the route is not mapped at all, so it answers 404 exactly like any
/// other path that does not exist. Turning it on makes every personal key a full browser login for
/// its owner, so enable it only on an instance where that is intended.
/// </remarks>
public class ApiKeyLoginSettings
{
    /// <summary>
    /// <c>Blocwerk:Auth:ApiKeyLogin:Enabled</c>, env <c>BLOCWERK__AUTH__APIKEYLOGIN__ENABLED</c> or
    /// <c>AUTH__APIKEYLOGIN__ENABLED</c>. Anything but a parseable <c>true</c> leaves it off.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The only users who may sign in with a key: <c>Blocwerk:Auth:ApiKeyLogin:AllowedUserIds</c>
    /// (a list of user ids; env <c>BLOCWERK__AUTH__APIKEYLOGIN__ALLOWEDUSERIDS__0</c>, <c>__1</c>, … or
    /// the comma-separated <c>AUTH__APIKEYLOGIN__ALLOWEDUSERIDS</c>). Empty means NOBODY, even when
    /// <see cref="Enabled"/> is set. A listed user may sign in with a key even when they have a second
    /// factor switched on — listing them is that decision.
    /// </summary>
    public IReadOnlySet<Guid> AllowedUserIds { get; set; } = new HashSet<Guid>();
}
