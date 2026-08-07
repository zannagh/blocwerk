namespace Blocwerk.Core.Enums;

/// <summary>Which TopLogger backend a connection authenticated against.</summary>
public enum TopLoggerBackend
{
    /// <summary>Not yet determined.</summary>
    Unknown = 0,

    /// <summary>Legacy REST API at api.toplogger.nu/v1.</summary>
    Legacy = 1,

    /// <summary>Newer GraphQL platform at app.toplogger.com.</summary>
    Modern = 2,
}
