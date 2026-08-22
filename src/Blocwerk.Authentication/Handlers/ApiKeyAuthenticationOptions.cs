using Microsoft.AspNetCore.Authentication;

namespace Blocwerk.Authentication.Handlers;

/// <summary>
/// Options for the <see cref="ApiKeyAuthenticationHandler"/>. The scheme has nothing to configure
/// today; the type exists because <see cref="AuthenticationHandler{TOptions}"/> requires one.
/// </summary>
public class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions
{
}
