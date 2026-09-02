namespace Blocwerk.Web.Maintenance;

/// <summary>What an avatar normalisation pass did, or would do in a dry run.</summary>
/// <param name="DryRun">True when nothing was written.</param>
/// <param name="Examined">Avatars looked at.</param>
/// <param name="Rewritten">Avatars replaced (or that would be).</param>
/// <param name="Skipped">Avatars already within the threshold, or that the pipeline could not
/// improve on.</param>
/// <param name="Failed">Avatars that could not be decoded or re-encoded.</param>
/// <param name="BytesBefore">Total stored size of the avatars in scope, before.</param>
/// <param name="BytesAfter">Total stored size of those same avatars, after.</param>
/// <param name="Elapsed">Wall-clock duration.</param>
public sealed record AvatarNormalizeSummary(
    bool DryRun,
    int Examined,
    int Rewritten,
    int Skipped,
    int Failed,
    long BytesBefore,
    long BytesAfter,
    TimeSpan Elapsed)
{
    public override string ToString()
    {
        var verb = DryRun ? "would rewrite" : "rewrote";
        var saved = BytesBefore - BytesAfter;

        return $"{Examined} avatars examined, {verb} {Rewritten} " +
            $"({Format(BytesBefore)} -> {Format(BytesAfter)}, {Format(saved)} saved), " +
            $"{Skipped} already fine, {Failed} failed, in {Elapsed.TotalSeconds:F1}s." +
            (DryRun ? " DRY RUN: nothing was written." : string.Empty);
    }

    private static string Format(long value) => value switch
    {
        >= 1024L * 1024 => $"{value / 1024d / 1024d:F1} MB",
        >= 1024 => $"{value / 1024d:F0} kB",
        _ => $"{value} B",
    };
}
