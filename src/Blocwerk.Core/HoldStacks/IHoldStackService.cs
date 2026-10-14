// <copyright file="IHoldStackService.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.HoldStacks;

/// <summary>
/// Stacks for the holds a wall already has. New holds are stacked when they are created; holds from before are only
/// ever stacked by this explicit action of a wall admin, which can first say what it would do (<see cref="PreviewAsync"/>)
/// and then do it (<see cref="ApplyAsync"/>). Never run at startup.
/// </summary>
public interface IHoldStackService
{
    /// <summary>What <see cref="ApplyAsync"/> would stack on the wall's live photos now; writes nothing.</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The proposals.</returns>
    Task<HoldStackReport> PreviewAsync(Guid wallId, CancellationToken ct = default);

    /// <summary>
    /// Stacks every live hold without a base that sits on another hold by the rule of <see cref="HoldStacking"/>, as
    /// one change-journal batch. Holds that already sit on a hold are left as they are, so a second run changes nothing.
    /// </summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="holdIds">Only these holds (the proposals somebody accepted); null for every proposal.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>What was stacked.</returns>
    Task<HoldStackReport> ApplyAsync(Guid wallId, IReadOnlyCollection<Guid>? holdIds = null, CancellationToken ct = default);
}

/// <summary>One hold that sits on another by its outline.</summary>
/// <param name="HoldId">The hold on top.</param>
/// <param name="BaseHoldId">The hold it sits on.</param>
/// <param name="PanelId">Their photo.</param>
/// <param name="InsidePercent">How much of the hold lies inside the base's outline, 0..100.</param>
/// <param name="AreaRatio">The base's area over the hold's.</param>
/// <param name="Color">The hold's colour.</param>
/// <param name="BaseColor">The base's colour.</param>
public sealed record HoldStackProposal(Guid HoldId, Guid BaseHoldId, Guid? PanelId, int InsidePercent, double AreaRatio, string? Color, string? BaseColor);

/// <summary>A preview or an applied run.</summary>
/// <param name="Applied">False for a preview.</param>
/// <param name="LiveHolds">The wall's live holds.</param>
/// <param name="AlreadyStacked">How many of them sat on a hold before.</param>
/// <param name="Stacks">The holds to stack (a preview), or the holds that were stacked.</param>
/// <param name="BatchId">The change-journal batch of an applied run that changed something.</param>
public sealed record HoldStackReport(bool Applied, int LiveHolds, int AlreadyStacked, IReadOnlyList<HoldStackProposal> Stacks, Guid? BatchId = null);
