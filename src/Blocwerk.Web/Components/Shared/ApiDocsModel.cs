namespace Blocwerk.Web.Components.Shared;

/// <summary>One path or query parameter of a documented endpoint.</summary>
/// <param name="Name">The parameter name as it appears in the route or query string.</param>
/// <param name="Location">Where it goes: "path" or "query".</param>
/// <param name="Description">A one-line explanation, including defaults where relevant.</param>
public sealed record ApiParamDoc(string Name, string Location, string Description);

/// <summary>A single documented endpoint: verb, path, params and example payloads.</summary>
/// <param name="Method">The HTTP verb, e.g. "GET" or "POST".</param>
/// <param name="Path">The route template relative to the host.</param>
/// <param name="Summary">A one-line description of what the endpoint does.</param>
/// <param name="Parameters">Path and query parameters, empty when there are none.</param>
/// <param name="RequestJson">Example request body, or null for endpoints without one.</param>
/// <param name="ResponseJson">Example success response body, or null for empty responses.</param>
/// <param name="ResponseNote">A short note shown in place of, or beside, the response.</param>
public sealed record ApiEndpointDoc(
    string Method,
    string Path,
    string Summary,
    IReadOnlyList<ApiParamDoc> Parameters,
    string? RequestJson,
    string? ResponseJson,
    string? ResponseNote = null);

/// <summary>A group of endpoints sharing one base path and one key scope.</summary>
/// <param name="Title">Human-readable name of the surface.</param>
/// <param name="Description">A sentence or two on what the surface is for.</param>
/// <param name="KeyScope">Which kind of API key it needs, e.g. "User key" or "Wall key".</param>
/// <param name="Endpoints">The endpoints in this surface.</param>
public sealed record ApiSurfaceDoc(
    string Title,
    string Description,
    string KeyScope,
    IReadOnlyList<ApiEndpointDoc> Endpoints);
