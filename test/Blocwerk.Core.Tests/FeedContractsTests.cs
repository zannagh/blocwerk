// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Reflection;
using System.Text.Json;
using Blocwerk.Contracts.Feed;
using Blocwerk.Contracts.Feed.Payloads;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The wire contract (design §5.4): payloads carry ids, enums, flags and generations only, and every property is pinned
/// here, so a new one (a name, a text) cannot slip in unreviewed; the envelope has the v1 shape; Contracts stays free of
/// server references so the WASM client can take it.
/// </summary>
public sealed class FeedContractsTests
{
    private static readonly Dictionary<string, string[]> PinnedPayloads = new()
    {
        [nameof(ActivityPayload)] = ["Sv", "Type"],
        [nameof(BoulderPayload)] = ["Generation", "IsDraft", "Sv"],
        [nameof(CommentPayload)] = ["BoulderId", "Sv"],
        [nameof(GenerationPayload)] = ["Generation", "Sv"],
        [nameof(HoldPayload)] = ["Generation", "PanelId", "Sv"],
        [nameof(MemberPayload)] = ["Role", "Sv"],
        [nameof(ResyncPayload)] = ["Reason", "Sv"],
        [nameof(VideoPayload)] = ["BoulderId", "EncodingStatus", "HasHls", "Sv"],
        [nameof(WallPayload)] = ["Generation", "OwnerChanged", "Sv"],
    };

    [Fact]
    public void EveryPayload_HasExactlyItsPinnedProperties()
    {
        var payloads = typeof(FeedPayload).Assembly.GetTypes()
            .Where(t => t.IsSubclassOf(typeof(FeedPayload)) && !t.IsAbstract)
            .ToDictionary(t => t.Name, t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.Name != "EqualityContract").Select(p => p.Name).Order().ToArray());

        Assert.Equal(PinnedPayloads.Keys.Order(), payloads.Keys.Order());
        Assert.All(PinnedPayloads, p => Assert.Equal(p.Value, payloads[p.Key]));
    }

    [Fact]
    public void APayload_SerializesCamelCase_WithItsVersion()
    {
        var json = FeedJson.Serialize(new HoldPayload(6, null));

        Assert.Equal("{\"generation\":6,\"sv\":1}", json);
    }

    [Fact]
    public void TheEnvelope_HasTheV1Shape()
    {
        var envelope = new FeedEnvelope
        {
            Cursor = "e.1.2",
            Kind = FeedKinds.CommentAdded,
            Actor = new FeedActor(Guid.NewGuid(), "Sam", HasAvatar: false, AvatarSha: null),
            Data = JsonSerializer.Deserialize<JsonElement>(FeedJson.Serialize(new CommentPayload(Guid.NewGuid()))),
            Hydrated = new FeedContent("hi", DateTimeOffset.UnixEpoch),
        };

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(envelope, FeedJson.Options));
        var keys = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();

        Assert.Equal(new[] { "v", "cursor", "kind", "at", "actor", "data", "hydrated", "tombstone" }, keys);
        Assert.Equal(1, doc.RootElement.GetProperty("data").GetProperty("sv").GetInt32());
    }

    [Fact]
    public void UnknownKindsAndVersions_AreRecognisedAsSuch()
    {
        Assert.True(FeedKinds.IsKnown(FeedKinds.WallResync));
        Assert.False(FeedKinds.IsKnown("wall.exploded"));
        Assert.True(FeedSchema.IsSupported(FeedSchema.PayloadVersion));
        Assert.False(FeedSchema.IsSupported(FeedSchema.PayloadVersion + 1));
    }

    [Fact]
    public void Contracts_ReferencesNoServerAssembly()
    {
        var references = typeof(FeedEnvelope).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        Assert.DoesNotContain(references, r => r.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
            || r.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) || r.StartsWith("Blocwerk.", StringComparison.Ordinal));
    }
}
