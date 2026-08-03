using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Services;

/// <summary>
/// Outcome of a confirmed wall recreation.
/// </summary>
public record WallRecreateResult(Wall Wall, int BouldersMadeHistoric, int HoldsPruned);
