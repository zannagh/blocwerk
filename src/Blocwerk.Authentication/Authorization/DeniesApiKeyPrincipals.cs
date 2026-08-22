namespace Blocwerk.Authentication.Authorization;

/// <summary>
/// Endpoint metadata stating that this route rejects API-key principals outright. It is attached
/// by <c>ApiKeyEndpointExtensions.DenyApiKeyPrincipals</c>, which also installs the filter that
/// enforces it, so the marker cannot drift away from the behaviour it claims.
/// </summary>
/// <remarks>
/// Routes under an <see cref="ApiKeySurface"/> prefix that serve the browser AND anonymous
/// share-token viewers cannot carry <c>[Authorize]</c> — it would break the share links — so this
/// is how they satisfy the "no unguarded endpoint under a covered prefix" rule.
/// </remarks>
public sealed class DeniesApiKeyPrincipals;
