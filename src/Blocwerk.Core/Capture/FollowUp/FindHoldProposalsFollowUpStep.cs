// <copyright file="FindHoldProposalsFollowUpStep.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Security.Cryptography;
using System.Text;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Capture.FollowUp;

/// <summary>
/// Step 5 (after the capture completed): holds seen in several capture photos that match no existing hold become
/// proposals for a wall admin to review (<see cref="IHoldProposalService.FindFromPipelineAsync"/>). It only
/// proposes: accepting one adds the hold to its panel (panel truth), so nothing live changes here. Minutes on the
/// CPU, so it runs once the capture already shows as done; it runs again only when the visible volumes changed
/// (known holds are subtracted at their volume points), and a re-run reuses the cached photo detections.
/// </summary>
public sealed class FindHoldProposalsFollowUpStep(IHoldProposalService proposals, IDbContextFactory<BlocwerkDbContext> dbContextFactory)
    : ICaptureFollowUpStep
{
    /// <inheritdoc />
    public string Key => "find-hold-proposals";

    /// <inheritdoc />
    public int Order => 400;

    /// <inheritdoc />
    public string Title => "Finding new holds in the capture photos";

    /// <inheritdoc />
    public bool NeedsPhotoReal => false;

    /// <inheritdoc />
    public bool RunsAfterCompletion => true;

    /// <inheritdoc />
    public async Task<string?> InputsKeyAsync(CaptureFollowUpContext context, CancellationToken ct)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var volumes = await db.WallVolumes.AsNoTracking()
            .Where(v => v.GeometryModelId == context.ModelId && !v.IsHidden)
            .Select(v => new { v.FacetId, v.SurfaceJson })
            .ToListAsync(ct);
        if (volumes.Count == 0)
        {
            return "volumes:none";
        }

        var text = string.Join('\n', volumes.Select(v => $"{v.FacetId}\t{v.SurfaceJson}").Order(StringComparer.Ordinal));
        return $"volumes:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16]}";
    }

    /// <inheritdoc />
    public async Task<CaptureFollowUpStepResult> RunAsync(CaptureFollowUpContext context, CancellationToken ct)
    {
        var result = await proposals.FindFromPipelineAsync(context.WallId, ct);
        if (result is null)
        {
            return CaptureFollowUpStepResult.Skipped("the capture photos could not be searched for holds");
        }

        return CaptureFollowUpStepResult.Done(Describe(result.Proposals));
    }

    /// <summary>"9 possible new holds to review" (empty when there are none).</summary>
    internal static string Describe(int proposals) =>
        proposals <= 0 ? string.Empty : $"{CaptureFollowUpText.Count(proposals, "possible new hold", "possible new holds")} to review";
}
