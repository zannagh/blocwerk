using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Services;

/// <summary>
/// A portable, self-contained snapshot of one or more journal batches: the batch/entry rows in
/// apply order, the byte[] blobs they reference (deduplicated), a base marker per scope, and the
/// source instance + EF migration id used for the schema-parity / divergence fail-fast on replay.
/// Serialized to JSON to move a verified local change to another environment ("prod").
/// </summary>
public sealed class ChangeJournalPackage
{
    /// <summary>The instance/environment the batches were recorded on (diagnostic).</summary>
    public string? SourceInstanceId { get; set; }

    /// <summary>The last EF migration applied on the source, checked against the target for schema parity.</summary>
    public string MigrationId { get; set; } = string.Empty;

    public List<ChangeJournalPackageBatch> Batches { get; set; } = [];

    /// <summary>Entries across all batches, already ordered for a forward apply (batch time, then Seq).</summary>
    public List<ChangeJournalPackageEntry> Entries { get; set; } = [];

    public List<ChangeJournalPackageBlob> Blobs { get; set; } = [];

    /// <summary>One base-state marker per distinct scoped aggregate, for the pre-apply divergence check.</summary>
    public List<ChangeJournalPackageBaseMarker> BaseMarkers { get; set; } = [];
}

/// <summary>A batch header carried in a <see cref="ChangeJournalPackage"/>.</summary>
public sealed class ChangeJournalPackageBatch
{
    public Guid Id { get; set; }

    public string Label { get; set; } = string.Empty;

    public ChangeJournalScopeKind ScopeKind { get; set; }

    public Guid? ScopeId { get; set; }

    public string? Actor { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A single row mutation carried in a <see cref="ChangeJournalPackage"/>.</summary>
public sealed class ChangeJournalPackageEntry
{
    public Guid BatchId { get; set; }

    public int Seq { get; set; }

    public string EntityType { get; set; } = string.Empty;

    public string KeyJson { get; set; } = string.Empty;

    public ChangeJournalOp Op { get; set; }

    public string? BeforeJson { get; set; }

    public string? AfterJson { get; set; }
}

/// <summary>A byte[] blob carried in a <see cref="ChangeJournalPackage"/>, keyed by content hash.</summary>
public sealed class ChangeJournalPackageBlob
{
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>The raw bytes, Base64-encoded for JSON transport.</summary>
    public string Base64 { get; set; } = string.Empty;

    public long Len { get; set; }
}

/// <summary>
/// A coarse fingerprint of a scoped aggregate at export time. On replay the target must still hold
/// the scope (a missing wall is a hard divergence); the generation/hash are compared for reporting,
/// while the authoritative per-row before-image checks catch finer divergence.
/// </summary>
public sealed class ChangeJournalPackageBaseMarker
{
    public ChangeJournalScopeKind ScopeKind { get; set; }

    public Guid? ScopeId { get; set; }

    public int Generation { get; set; }

    public string LiveHash { get; set; } = string.Empty;
}
