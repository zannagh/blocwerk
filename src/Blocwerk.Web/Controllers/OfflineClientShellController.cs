// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Web.Components.OfflineClient;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// The offline gym client's shell for every path under <c>/v/</c> that names no file, so the client's own router opens
/// it. A controller because controller endpoints carry the fingerprinted asset map (<c>MapControllers().WithStaticAssets()</c>),
/// which the shell's <c>@Assets</c> and import map read; minimal-API routes have no such hook. The client's files
/// (<c>/v/_framework/…</c>, <c>/v/css/…</c>, <c>/v/js/…</c>) are static assets; a missing one is a 404, never the shell.
/// </summary>
/// <remarks>
/// Nothing links here yet and the service worker does not redirect here until PR 3.9, so the Server pages are
/// unchanged. The shell is the same for every caller and carries no identity, so it is anonymous. It is revalidated
/// on every load (<c>no-cache</c>): it names this deploy's fingerprinted files.
/// </remarks>
[AllowAnonymous]
public sealed class OfflineClientShellController : Controller
{
    [HttpGet("/v/{**path:nonfile}")]
    public RazorComponentResult<OfflineClientShell> Shell()
    {
        Response.Headers[HeaderNames.CacheControl] = "no-cache";
        return new RazorComponentResult<OfflineClientShell>();
    }
}
