// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.Endpoints;

/// <summary>
/// The response of every working-data document (<c>/d/{kind}/{sha}</c>): <c>private, no-cache</c> under the strong
/// <c>ETag: "{sha}"</c>, a 304 only after the gate, Brotli when the browser accepts it, and <c>no-store</c> with
/// <c>Vary: Cookie</c> where a kiosk session may not keep the bytes.
/// </summary>
internal static class DocumentResponse
{
    public static IResult Write(HttpContext http, string sha, byte[] json, byte[] brotliJson, bool noStore)
    {
        var etag = $"\"{sha}\"";
        if (noStore)
        {
            http.Response.Headers.CacheControl = AssetResponse.NoStoreCacheControl;
            http.Response.Headers.Vary = "Cookie, Accept-Encoding";
        }
        else
        {
            http.Response.Headers.ETag = etag;
            http.Response.Headers.CacheControl = AssetResponse.RevalidateCacheControl;
            http.Response.Headers.Vary = "Accept-Encoding";
            if (ImageResponse.Matches(http.Request, etag))
            {
                return Results.StatusCode(StatusCodes.Status304NotModified);
            }
        }

        var brotli = AcceptsBrotli(http.Request);
        var body = brotli ? brotliJson : json;
        if (brotli)
        {
            http.Response.Headers.ContentEncoding = "br";
        }

        if (HttpMethods.IsHead(http.Request.Method))
        {
            http.Response.ContentType = "application/json";
            http.Response.ContentLength = body.LongLength;
            return Results.Empty;
        }

        return Results.Bytes(body, "application/json");
    }

    // "br" listed in Accept-Encoding with any weight but q=0.
    private static bool AcceptsBrotli(HttpRequest request) =>
        request.GetTypedHeaders().AcceptEncoding.Any(e =>
            e.Value.Equals("br", StringComparison.OrdinalIgnoreCase) && (e.Quality ?? 1) > 0);
}
