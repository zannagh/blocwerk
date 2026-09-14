using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Blocwerk.Core.Data;

/// <summary>
/// Data-safety guard for replay/revert deletes of a cascade PARENT. A raw <c>DELETE</c> of a
/// principal (Boulder → cascades BoulderHold; Wall → cascades holds/boulders/panels/segments/…)
/// would silently take every dependent row with it — including rows a human added on the target
/// that the batch never knew about. Before such a delete is applied we enumerate the principal's
/// cascade dependents and report any that are NOT themselves being deleted by the same batch, so
/// the run can raise a conflict instead of quietly cascading.
/// </summary>
/// <remarks>
/// Generic and metadata-driven: it walks the principal's referencing foreign keys, keeps only the
/// ones whose delete behaviour actually cascades, and enumerates each through its principal-side
/// collection navigation. Among the allow-listed cascade parents this covers Boulder
/// (<c>BoulderHolds</c>, <c>Attempts</c>) and Wall (<c>Holds</c>, <c>Boulders</c>, <c>Segments</c>,
/// <c>Members</c>, …). A cascade relationship that carries NO principal-side navigation cannot be
/// enumerated this way and is skipped; the allow-listed parents all expose one for their dependents.
/// FK-Restrict dependents (e.g. Hold ← BoulderHold) are not cascades and abort safely at the store,
/// so they are intentionally out of scope here.
/// </remarks>
internal static class ChangeJournalCascadeGuard
{
    /// <summary>
    /// Returns a human-readable description of each cascade dependent of <paramref name="principal"/>
    /// that would be silently removed — i.e. is not present in <paramref name="deletedByBatch"/>
    /// (the set of <c>(EntityType, KeyJson)</c> rows this batch deletes). Empty when the delete is safe.
    /// </summary>
    public static async Task<List<string>> FindBlockingDependentsAsync(
        DbContext context,
        EntityEntry principal,
        IReadOnlySet<(string EntityType, string KeyJson)> deletedByBatch,
        CancellationToken cancellationToken)
    {
        var blocking = new List<string>();
        foreach (var foreignKey in principal.Metadata.GetReferencingForeignKeys())
        {
            if (foreignKey.DeleteBehavior is not (DeleteBehavior.Cascade or DeleteBehavior.ClientCascade))
            {
                continue;
            }

            var navigation = foreignKey.PrincipalToDependent;
            if (navigation is null || !navigation.IsCollection)
            {
                continue;
            }

            var collection = principal.Collection(navigation.Name);
            await collection.LoadAsync(cancellationToken);
            if (collection.CurrentValue is null)
            {
                continue;
            }

            foreach (var dependent in collection.CurrentValue)
            {
                var dependentEntry = context.Entry(dependent);
                var key = (dependentEntry.Metadata.ClrType.Name, ChangeJournalValueWriter.SerializeKey(dependentEntry));
                if (!deletedByBatch.Contains(key))
                {
                    blocking.Add($"{key.Item1} {key.Item2}");
                }
            }
        }

        return blocking;
    }
}
