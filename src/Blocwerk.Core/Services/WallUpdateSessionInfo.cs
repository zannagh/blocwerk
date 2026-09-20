using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Services;

/// <summary>
/// A read-only view of an in-flight wall update for the wizard: where to resume, and who has it open.
/// </summary>
/// <param name="Id">The session row.</param>
/// <param name="WallId">The wall being updated.</param>
/// <param name="StagedGeneration">The generation the staged panels and holds carry.</param>
/// <param name="Phase">The step to resume at.</param>
/// <param name="NeighbourIndex">How far into the neighbour-overlap walk the user had got.</param>
/// <param name="CreatedAt">When the update was started.</param>
/// <param name="CreatedByUserId">Who started it.</param>
/// <param name="CreatedByName">That user's display name, for "started by X at T". Null if unknown.</param>
/// <param name="UpdatedAt">When it was last written to.</param>
/// <param name="LastActiveByUserId">Who wrote to it last — not necessarily its creator.</param>
/// <param name="LastActiveByName">That user's display name. Null if unknown.</param>
public record WallUpdateSessionInfo(
    Guid Id,
    Guid WallId,
    int StagedGeneration,
    WallUpdatePhase Phase,
    int NeighbourIndex,
    DateTimeOffset CreatedAt,
    Guid? CreatedByUserId,
    string? CreatedByName,
    DateTimeOffset UpdatedAt,
    Guid? LastActiveByUserId,
    string? LastActiveByName);
