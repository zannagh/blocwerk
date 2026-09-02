using Blocwerk.Authentication;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// Mints the antiforgery request token that the client-side offline queue attaches to every
/// mutation it replays (see <see cref="OfflineActionsController"/> and
/// <see cref="OfflineBouldersController"/>, whose POSTs all carry
/// <c>[ValidateAntiForgeryToken]</c>).
/// </summary>
/// <remarks>
/// A token is fetched at flush time rather than held across the offline period, and re-fetched once
/// if a POST is rejected: the token is bound to the principal it was minted for, so one issued
/// before a re-login — on a shared tablet, say — is worthless afterwards. The same call also sets
/// the matching antiforgery cookie, which is the half the browser keeps.
/// </remarks>
[ApiController]
[Route("api/offline/antiforgery")]
[Authorize]
[RequireClientHeader]
[Produces("application/json")]
public sealed class OfflineAntiforgeryController : ControllerBase
{
    private readonly IAntiforgery antiforgery;

    public OfflineAntiforgeryController(IAntiforgery antiforgery)
    {
        this.antiforgery = antiforgery;
    }

    [HttpGet]
    public IActionResult Get()
    {
        var tokens = antiforgery.GetAndStoreTokens(HttpContext);
        return Ok(new
        {
            header = AuthenticationServices.AntiforgeryHeaderName,
            token = tokens.RequestToken,
        });
    }
}
