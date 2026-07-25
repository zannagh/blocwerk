namespace Blocwerk.Core.Enums;

/// <summary>
/// How a hold may be used within a boulder.
/// </summary>
public enum HoldUsage
{
    /// <summary>The hold may be used with both hands and feet. Default.</summary>
    HandAndFoot = 0,

    /// <summary>The hold may only be used with hands.</summary>
    HandOnly = 1,

    /// <summary>The hold may only be used with feet.</summary>
    FootOnly = 2,
}
