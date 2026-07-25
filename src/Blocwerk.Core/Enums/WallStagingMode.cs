namespace Blocwerk.Core.Enums;

public enum WallStagingMode
{
    None = 0,
    Detected = 1,
    Manual = 2,

    /// <summary>Full rebuild: the previous hold model is replaced rather than carried forward.</summary>
    Recreate = 3,
}
