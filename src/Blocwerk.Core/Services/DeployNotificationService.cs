using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>
/// Fires the deploy notifications once, and ONLY on a genuine new build. Registered as a hosted
/// service so it runs once per process, off the startup critical path (a <see cref="BackgroundService"/>
/// <c>ExecuteAsync</c>, never blocking <c>StartAsync</c>). Nothing in here may throw out of startup:
/// every file operation is wrapped and degrades to "treat as a new build" so a first run still notifies.
/// </summary>
/// <remarks>
/// <b>Build identity.</b> The build id is the entry assembly's
/// <see cref="Module.ModuleVersionId"/> (MVID) — a GUID baked into the compiled binary: identical
/// across restarts of the same image, different for a new build. It is persisted to a marker file in
/// the app's persisted directory (<c>Deploy:StatePath</c>, defaulting to the DataProtection keys dir
/// <c>/app/keys</c> that the compose mounts as <c>./dpkeys</c> and survives redeploys). On startup: if
/// the persisted MVID equals the current one it is a restart (crash/reboot) and NO ONE is notified; if
/// it differs (or no marker exists) it is a real deployment and we notify, then persist the new MVID.
/// <para>
/// <b>Who is notified.</b> On a real deployment we branch on the maintenance signal so admins get
/// exactly one toast per deploy either way: maintenance ON broadcasts "app back online" to ALL
/// subscribers (admins included) via <see cref="IPushNotificationService.NotifyAppOnlineAsync"/> and
/// nothing else; maintenance OFF notifies admins only via
/// <see cref="IPushNotificationService.NotifyDeploymentAsync"/>. Regular users are pinged only on a
/// maintenance deploy.
/// </para>
/// <para>
/// <b>Maintenance signal.</b> True if EITHER the <c>Deploy:MaintenanceWindow</c> flag parses to true
/// (env <c>DEPLOY__MAINTENANCEWINDOW=true</c>) OR a one-shot marker file
/// <c>&lt;StatePath&gt;/maintenance-window</c> exists. To notify all users once for a maintenance
/// deploy without editing per-deploy env, <c>touch keys/maintenance-window</c> before the deploy; the
/// marker is DELETED after firing so a cron autodeploy never repeats the all-users broadcast.
/// </para>
/// </remarks>
public sealed class DeployNotificationService : BackgroundService
{
    private const string BuildIdMarkerName = "deploy-build-id";
    private const string MaintenanceMarkerName = "maintenance-window";

    private readonly IPushNotificationService pushNotificationService;
    private readonly bool maintenanceWindowFlag;
    private readonly string statePath;
    private readonly ILogger<DeployNotificationService> logger;

    public DeployNotificationService(
        IPushNotificationService pushNotificationService,
        IConfiguration configuration,
        ILogger<DeployNotificationService> logger)
    {
        this.pushNotificationService = pushNotificationService;
        this.logger = logger;

        // Total parse: an empty or malformed value (compose injects DEPLOY__MAINTENANCEWINDOW="")
        // must default to false, never throw out of the hosted-service constructor.
        this.maintenanceWindowFlag = bool.TryParse(configuration["Deploy:MaintenanceWindow"], out var mw) && mw;

        // The persisted state dir defaults to the DataProtection keys dir (mounted, survives redeploys).
        var configured = configuration["Deploy:StatePath"];
        this.statePath = string.IsNullOrWhiteSpace(configured) ? "/app/keys" : configured;
    }

    /// <summary>The three possible startup outcomes. Pure over its inputs, so it is unit-testable.</summary>
    internal enum DeployAction
    {
        /// <summary>Same binary as last notified — a restart. Notify no one.</summary>
        None,

        /// <summary>New build, routine deploy — notify site admins only.</summary>
        NotifyAdmins,

        /// <summary>New build, maintenance window — broadcast "app back online" to all subscribers.</summary>
        NotifyAllUsers,
    }

    /// <summary>
    /// Decides what a startup should do: a matching persisted build id means a restart (notify no one);
    /// a different or absent id means a real deployment, routed by the maintenance signal.
    /// </summary>
    internal static DeployAction DecideAction(string? persistedBuildId, string currentBuildId, bool maintenanceSignal)
    {
        if (!string.IsNullOrEmpty(persistedBuildId) && persistedBuildId == currentBuildId)
        {
            return DeployAction.None;
        }

        return maintenanceSignal ? DeployAction.NotifyAllUsers : DeployAction.NotifyAdmins;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var currentBuildId = CurrentBuildId();
            var persistedBuildId = ReadPersistedBuildId();
            var maintenanceMarkerExists = MaintenanceMarkerExists();
            var maintenanceSignal = maintenanceWindowFlag || maintenanceMarkerExists;

            var action = DecideAction(persistedBuildId, currentBuildId, maintenanceSignal);
            if (action == DeployAction.None)
            {
                logger.LogInformation("Restart of the same build ({BuildId}); no deploy notification sent.", currentBuildId);
                return;
            }

            await NotifyAsync(action);

            // One-shot: clear the maintenance marker (if it triggered) so a cron redeploy of the same
            // situation never repeats the all-users broadcast.
            if (maintenanceMarkerExists)
            {
                DeleteMaintenanceMarker();
            }

            WritePersistedBuildId(currentBuildId);
        }
        catch (Exception ex)
        {
            // A deploy notice must never take the process down on startup.
            logger.LogError(ex, "Deploy notification failed on startup and was suppressed.");
        }
    }

    private async Task NotifyAsync(DeployAction action)
    {
        if (action == DeployAction.NotifyAllUsers)
        {
            logger.LogInformation("Maintenance-window deploy: broadcasting app-online to all subscribers.");
            await pushNotificationService.NotifyAppOnlineAsync();
        }
        else
        {
            logger.LogInformation("Routine deploy: notifying site admins only.");
            await pushNotificationService.NotifyDeploymentAsync();
        }
    }

    private static string CurrentBuildId()
    {
        // MVID is stable across restarts of the same compiled binary and changes for a new build.
        return Assembly.GetEntryAssembly()?.ManifestModule.ModuleVersionId.ToString("N") ?? Guid.NewGuid().ToString("N");
    }

    private string? ReadPersistedBuildId()
    {
        try
        {
            var path = Path.Combine(statePath, BuildIdMarkerName);
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (Exception ex)
        {
            // Unreadable marker => treat as a new build so a first run still notifies.
            logger.LogWarning(ex, "Could not read deploy build-id marker in {StatePath}; treating as a new build.", statePath);
            return null;
        }
    }

    private void WritePersistedBuildId(string buildId)
    {
        try
        {
            Directory.CreateDirectory(statePath);
            File.WriteAllText(Path.Combine(statePath, BuildIdMarkerName), buildId);
        }
        catch (Exception ex)
        {
            // Non-fatal: worst case the next restart re-notifies once. Better than faulting startup.
            logger.LogWarning(ex, "Could not persist deploy build-id marker in {StatePath}.", statePath);
        }
    }

    private bool MaintenanceMarkerExists()
    {
        try
        {
            return File.Exists(Path.Combine(statePath, MaintenanceMarkerName));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not probe the maintenance-window marker in {StatePath}.", statePath);
            return false;
        }
    }

    private void DeleteMaintenanceMarker()
    {
        try
        {
            File.Delete(Path.Combine(statePath, MaintenanceMarkerName));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not clear the one-shot maintenance-window marker in {StatePath}.", statePath);
        }
    }
}
