using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Blocwerk.Core.Data;

/// <summary>
/// The NET effect a single batch produces on ONE key, reduced from that key's ordered entries. A key
/// can be written more than once inside a batch (a wall update INSERTs a staged row in the run phase
/// then UPDATEs it in the promote); replay and revert must decide against this net effect, never
/// against a single intermediate write — otherwise re-replaying an already-applied package reports
/// spurious conflicts, and revert fails to notice an out-of-band edit to a property the INSERT set
/// but a later UPDATE never touched.
/// </summary>
internal readonly record struct ChangeJournalKeyNetEffect
{
    private ChangeJournalKeyNetEffect(
        bool netIsAbsent,
        Dictionary<string, object?>? netFinalImage,
        bool firstRequiresAbsent,
        Dictionary<string, object?>? firstBeforeImage)
    {
        NetIsAbsent = netIsAbsent;
        NetFinalImage = netFinalImage;
        FirstRequiresAbsent = firstRequiresAbsent;
        FirstBeforeImage = firstBeforeImage;
    }

    /// <summary>True when the batch's net effect leaves the row absent (net delete, or an insert undone by a later delete).</summary>
    public bool NetIsAbsent { get; }

    /// <summary>
    /// The full property image the batch produces, or null when <see cref="NetIsAbsent"/>. For an
    /// insert-first key this is the insert's after-image with every later update overlaid; for an
    /// update-first key it is the touched properties at their final values (the batch never captured
    /// the untouched columns, so they are not part of the net effect).
    /// </summary>
    public Dictionary<string, object?>? NetFinalImage { get; }

    /// <summary>True when the batch's first write to this key is an Insert, so a fresh apply requires the row ABSENT.</summary>
    public bool FirstRequiresAbsent { get; }

    /// <summary>The first entry's before-image — what a fresh apply requires the target to match — when the batch does NOT start with an Insert.</summary>
    public Dictionary<string, object?>? FirstBeforeImage { get; }

    /// <summary>
    /// Reduces one key's entries (ascending <c>Seq</c>) to their net effect. Walk in order: an Insert
    /// replaces the running image with its full after-image; an Update overlays its changed props onto
    /// the running image (seeding it from the first update's before-image when the key is update-first,
    /// so the touched columns are represented); a Delete clears it. What remains is the net final image
    /// (null == the row ends absent).
    /// </summary>
    public static ChangeJournalKeyNetEffect Reduce(
        IEntityType entityType,
        IReadOnlyList<(ChangeJournalOp Op, string? BeforeJson, string? AfterJson)> orderedEntries,
        Func<string, byte[]?> resolveBlob)
    {
        Dictionary<string, object?>? image = null;
        foreach (var (op, beforeJson, afterJson) in orderedEntries)
        {
            switch (op)
            {
                case ChangeJournalOp.Insert:
                    image = ChangeJournalValueWriter.DeserializeValues(entityType, afterJson ?? "{}", resolveBlob);
                    break;

                case ChangeJournalOp.Update:
                    image ??= ChangeJournalValueWriter.DeserializeValues(entityType, beforeJson ?? "{}", resolveBlob);
                    foreach (var (name, value) in ChangeJournalValueWriter.DeserializeValues(entityType, afterJson ?? "{}", resolveBlob))
                    {
                        image[name] = value;
                    }

                    break;

                case ChangeJournalOp.Delete:
                    image = null;
                    break;
            }
        }

        var first = orderedEntries[0];
        var firstRequiresAbsent = first.Op == ChangeJournalOp.Insert;
        var firstBeforeImage = firstRequiresAbsent
            ? null
            : ChangeJournalValueWriter.DeserializeValues(entityType, first.BeforeJson ?? "{}", resolveBlob);

        return new ChangeJournalKeyNetEffect(image is null, image, firstRequiresAbsent, firstBeforeImage);
    }
}
