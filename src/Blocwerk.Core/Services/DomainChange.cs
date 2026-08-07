namespace Blocwerk.Core.Services;

/// <summary>
/// A notification that some domain data changed, broadcast in-process by
/// <see cref="IDomainChangeNotifier"/> so per-circuit caches can drop stale entries and open
/// views can live-refresh. Deliberately tiny: identifiers only, never entity graphs.
/// </summary>
/// <param name="Scope">Which kind of data changed.</param>
/// <param name="WallId">The wall involved, or <see cref="System.Guid.Empty"/> when not applicable.</param>
/// <param name="BoulderId">The boulder involved, or <see cref="System.Guid.Empty"/> when not applicable.</param>
public readonly record struct DomainChange(DomainChangeScope Scope, Guid WallId, Guid BoulderId);
