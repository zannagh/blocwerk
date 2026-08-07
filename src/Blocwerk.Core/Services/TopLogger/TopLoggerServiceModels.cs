namespace Blocwerk.Core.Services.TopLogger;

/// <summary>Connection state shown on the profile page.</summary>
public record TopLoggerStatus(
    bool Connected,
    bool Enabled,
    string? Email,
    DateTimeOffset? LastSyncAt,
    string? LastError,
    int AscentCount);

/// <summary>Outcome of a connect or sync operation.</summary>
public record TopLoggerSyncResult(bool Success, int Imported, string? Error);
