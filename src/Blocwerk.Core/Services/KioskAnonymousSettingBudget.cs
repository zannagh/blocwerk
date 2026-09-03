namespace Blocwerk.Core.Services;

/// <summary>
/// Which volume budget an anonymous kiosk create fell foul of, or that it fell foul of none.
/// </summary>
/// <remarks>
/// A single bool used to be enough, back when there was one shared counter. There no longer is:
/// the three scopes mean three very different things operationally, and the installation-wide one
/// is the only one that should ever reach an operator's attention.
/// </remarks>
public enum KioskAnonymousSettingBudget
{
    /// <summary>Inside every budget; the write may proceed.</summary>
    Allowed,

    /// <summary>This one tablet, on this one wall, has had its hour's worth.</summary>
    TabletCapReached,

    /// <summary>Every tablet on this wall together has had the wall's hour's worth.</summary>
    WallCapReached,

    /// <summary>
    /// The installation-wide backstop tripped. Set far above any plausible legitimate load, so this
    /// is not routine throttling — it means something is wrong and is logged as such.
    /// </summary>
    InstallationCapReached,
}
