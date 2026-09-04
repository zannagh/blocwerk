using Blocwerk.Core.Services;
using Xunit;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Pins the pure startup decision of <see cref="DeployNotificationService"/>: a matching persisted
/// build id is a restart (notify no one); a different or absent id is a real deployment, routed to
/// admins for a routine deploy and to all users for a maintenance window.
/// </summary>
public class DeployNotificationDecisionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SameBuildId_IsARestart_NotifiesNoOne(bool maintenanceSignal)
    {
        var action = DeployNotificationService.DecideAction("build-a", "build-a", maintenanceSignal);

        Assert.Equal(DeployNotificationService.DeployAction.None, action);
    }

    [Fact]
    public void DifferentBuildId_RoutineDeploy_NotifiesAdminsOnly()
    {
        var action = DeployNotificationService.DecideAction("build-a", "build-b", maintenanceSignal: false);

        Assert.Equal(DeployNotificationService.DeployAction.NotifyAdmins, action);
    }

    [Fact]
    public void DifferentBuildId_MaintenanceDeploy_BroadcastsToAllUsers()
    {
        var action = DeployNotificationService.DecideAction("build-a", "build-b", maintenanceSignal: true);

        Assert.Equal(DeployNotificationService.DeployAction.NotifyAllUsers, action);
    }

    [Fact]
    public void NoPersistedMarker_IsAFirstRun_TreatedAsANewDeployment()
    {
        var routine = DeployNotificationService.DecideAction(null, "build-a", maintenanceSignal: false);
        var maintenance = DeployNotificationService.DecideAction(string.Empty, "build-a", maintenanceSignal: true);

        Assert.Equal(DeployNotificationService.DeployAction.NotifyAdmins, routine);
        Assert.Equal(DeployNotificationService.DeployAction.NotifyAllUsers, maintenance);
    }
}
