namespace Blocwerk.Core.Services;

/// <summary>
/// A big-wall panel's grid placement, without its photo bytes. The image is fetched separately
/// from the panel photo (live) or staged-photo endpoint. Covers both live panels and staged-only
/// panels that are still mid-confirmation.
/// </summary>
/// <param name="Id">The panel's id.</param>
/// <param name="Col">Grid column; 0 is the center panel.</param>
/// <param name="Row">Grid row; 0 is the center panel.</param>
/// <param name="IsLive">True when the panel has a promoted (live) photo.</param>
/// <param name="HasStaged">True when the panel carries an unpromoted staged photo.</param>
/// <param name="Generation">The generation this panel's photo/holds live at.</param>
/// <param name="IsOutdated">
/// True when the panel lags the wall's current generation (<see cref="Generation"/> is below
/// <c>Wall.CurrentGeneration</c>). Purely derived from existing per-panel generations — a wall
/// whose panels all sit at the same generation has none outdated. Surfaces the "needs update"
/// state once a subset of panels has been promoted ahead of the rest.
/// </param>
public record WallPanelInfo(Guid Id, int Col, int Row, bool IsLive, bool HasStaged, int Generation, bool IsOutdated);
