using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.Services;
using Blocwerk.Web.Endpoints;
using Blocwerk.Web.State;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// Lets the autodeploy hook tell the still-live container "I am about to recreate you", so the
/// browsers and kiosk tablets connected to it can show a sentence instead of a bare reconnect
/// spinner when it disappears.
/// </summary>
/// <remarks>
/// Installation-scoped API key only, and the scheme is pinned so a browser cookie can never reach
/// it: this is a machine route, and the machine in question is the deploy hook. It writes nothing
/// but a field in memory, so there is no database work to abuse and no rate limiter beyond the key
/// itself — the message and the TTL are clamped by <see cref="IMaintenanceAnnouncer"/>, which is
/// what keeps an absurd body from turning into a permanent banner.
/// </remarks>
[ApiController]
[Route("api/v1/maintenance")]
[Authorize(Policy = BlocwerkPolicies.InstallationApiKey, AuthenticationSchemes = ApiKeyAuthenticationHandler.SchemeName)]
[Produces("application/json")]
public sealed class MaintenanceAnnouncementController : ControllerBase
{
    /// <summary>
    /// Caps the request body itself, well below anything the announcer would keep. Without it a
    /// megabyte of text would be read and parsed before being thrown away.
    /// </summary>
    private const int MaxBodyBytes = 4 * 1024;

    private readonly IMaintenanceAnnouncer announcer;
    private readonly ILogger<MaintenanceAnnouncementController> logger;

    public MaintenanceAnnouncementController(
        IMaintenanceAnnouncer announcer,
        ILogger<MaintenanceAnnouncementController> logger)
    {
        this.announcer = announcer;
        this.logger = logger;
    }

    /// <summary>
    /// Raises the "this server is about to be updated" notice and answers with the state as it was
    /// actually recorded — message and expiry after clamping, so the caller can log what landed
    /// rather than what it asked for.
    /// </summary>
    /// <remarks>
    /// Antiforgery is not applicable: the caller is a machine holding a bearer key, never a browser
    /// form, and no cookie can authorize this route.
    /// </remarks>
    [HttpPost("announce")]
    [RequestSizeLimit(MaxBodyBytes)]
    public ActionResult<AliveResponse> Announce([FromBody] MaintenanceAnnounceRequest? request)
    {
        var ttl = request?.EtaSeconds is { } seconds && seconds > 0
            ? TimeSpan.FromSeconds(Math.Min(seconds, (int)MaintenanceAnnouncer.MaxTtl.TotalSeconds))
            : TimeSpan.Zero;

        var announcement = announcer.Announce(request?.Message, ttl);

        logger.LogInformation(
            "Maintenance announced by API key {ApiKeyId} until {ExpiresAt} (message: {HasMessage})",
            User.FindFirst(ApiKeyClaimTypes.ApiKeyId)?.Value ?? "unknown",
            announcement.ExpiresAt,
            announcement.Message is not null);

        return Ok(new AliveResponse(
            ProcessInstance.Id,
            ProcessInstance.StartedAt,
            true,
            announcement.Message,
            announcement.ExpiresAt));
    }
}
