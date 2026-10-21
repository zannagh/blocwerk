// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;

namespace Blocwerk.Contracts.Feed;

/// <summary>
/// One delivered feed event (envelope v1, design §5.4), after hydration. <see cref="Data"/> is the stored, ids-only
/// payload; <see cref="Actor"/> and <see cref="Hydrated"/> are read from the source tables at delivery. Apply it by id:
/// events of different transactions may arrive out of commit order.
/// </summary>
public sealed record FeedEnvelope
{
    /// <summary>Gets the envelope version (<see cref="FeedSchema.EnvelopeVersion"/>).</summary>
    public int V { get; init; } = FeedSchema.EnvelopeVersion;

    /// <summary>Gets this event's cursor, <c>"{epoch}.{tx}.{seq}"</c>; a reader continues after it.</summary>
    public required string Cursor { get; init; }

    public Guid? WallId { get; init; }

    public Guid? BoulderId { get; init; }

    /// <summary>Gets the kind (<see cref="FeedKinds"/>).</summary>
    public required string Kind { get; init; }

    /// <summary>Gets the changed row's id.</summary>
    public Guid? EntityId { get; init; }

    /// <summary>Gets when the change was written.</summary>
    public DateTimeOffset At { get; init; }

    public FeedActor? Actor { get; init; }

    /// <summary>Gets the payload (<see cref="Payloads.FeedPayload"/>, with its <c>sv</c>), or null for coarse kinds.</summary>
    public JsonElement? Data { get; init; }

    /// <summary>Gets the content read at delivery, or null when the kind has none.</summary>
    public FeedContent? Hydrated { get; init; }

    /// <summary>Gets a value indicating whether the source row is gone: the client drops the item instead of showing it.</summary>
    public bool Tombstone { get; init; }
}
