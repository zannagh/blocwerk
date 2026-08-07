namespace Blocwerk.Core.Services;

/// <summary>What kind of domain data a <see cref="DomainChange"/> refers to.</summary>
public enum DomainChangeScope
{
    /// <summary>A wall aggregate (its metadata, holds, segments, or its boulder set) changed.</summary>
    Wall,

    /// <summary>A single boulder changed. Its wall aggregate is affected too.</summary>
    Boulder,

    /// <summary>The set of walls a user can see changed (wall created/deleted, membership).</summary>
    WallList,
}
