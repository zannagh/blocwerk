namespace Blocwerk.Core.Services.TopLogger;

/// <summary>Connects a user's TopLogger account and imports their ascents.</summary>
public interface ITopLoggerService
{
    Task<TopLoggerStatus> GetStatusAsync();

    /// <summary>Signs in, stores the (encrypted) token, and runs an immediate first sync.</summary>
    Task<TopLoggerSyncResult> ConnectAsync(string email, string password);

    Task<TopLoggerSyncResult> SyncNowAsync();

    Task DisconnectAsync(bool deleteImportedAscents);
}
