// <copyright file="WallFrameRegistrationWriter.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json.Nodes;

namespace Blocwerk.Core.Geometry.Registration;

/// <summary>Who and what a registered model was tied to, recorded in <c>quality.registration</c>.</summary>
/// <param name="ReferenceModelId">The active model it was registered to.</param>
/// <param name="ReferencePlanRevision">That model's plan revision (null: legacy).</param>
/// <param name="PlanRevision">The new model's plan revision (null: legacy).</param>
public sealed record RegistrationStamp(Guid ReferenceModelId, int? ReferencePlanRevision, int? PlanRevision);

/// <summary>
/// Rewrites a freshly solved <c>wall-geometry.json</c> into the reference model's frame (see
/// <see cref="WallFrameRegistration"/>), keeping every field it does not touch:
/// <list type="bullet">
/// <item>world coordinates (facet frames, marker world corners, cameras) are mapped by the fitted transform;</item>
/// <item>a new facet carrying the reference facet's unchanged markers TAKES that facet's id, so holds, volumes and
/// textures keep referring to it, but KEEPS its own solved plane and measured angles (a re-solve must be able to
/// improve the geometry); only its plane frame is rebased onto the reference one, so a hold's (facet, a, b)
/// still means (nearly) the same spot on the wall; its markers are re-projected into it;</item>
/// <item>reference facets and unchanged markers this capture did not photograph are carried over (a partial
/// re-capture of the changed areas keeps the rest of the wall);</item>
/// <item>the reference's <c>world</c> block is kept (its <c>up</c> is the new solve's, turned into the frame) and
/// <c>quality.registration</c> says what was done.</item>
/// </list>
/// </summary>
public static partial class WallFrameRegistrationWriter
{
    private const double MatchNormalToleranceDeg = 20;
    private const double ExtentMarginMm = 50;

    /// <summary>
    /// The solved document in the reference frame. <paramref name="carryIds"/>: reference markers that may be
    /// carried over when not re-photographed (the unchanged ids; null = all).
    /// </summary>
    public static string Rewrite(
        string solvedJson, string referenceJson, FrameRegistrationResult registration, IReadOnlySet<int>? carryIds, RegistrationStamp stamp)
    {
        var transform = registration.Transform ?? throw new ArgumentException("Only an accepted registration can be written.", nameof(registration));
        var solved = WallGeometryDocument.Parse(solvedJson);
        var reference = WallGeometryDocument.Parse(referenceJson);
        var root = JsonNode.Parse(solvedJson)!.AsObject();
        var referenceRoot = JsonNode.Parse(referenceJson)!.AsObject();

        var world = MarkerWorldCorners.Of(solved).ToDictionary(kv => kv.Key, kv => kv.Value.Select(transform.Apply).ToArray());
        TransformFacets(root, transform);
        TransformCameras(root, transform);

        var claims = MatchFacets(solved, reference, registration.UsedIds, transform);
        var carried = reference.Segments.SelectMany(s => s.Facets)
            .Where(f => MarkerWorldCorners.HasFrame(f) && !claims.Values.Contains(f.Id))
            .Select(f => f.Id)
            .ToList();
        var renamed = FinalIds(root, claims, carried);
        var rebased = RebaseClaimedFrames(root, reference, claims, renamed);
        RewriteMarkers(root, rebased, renamed, world);
        var carriedMarkers = Carry(root, referenceRoot, reference, carried, carryIds);
        RefreshMarkerIds(root);

        root["world"] = World(root, referenceRoot, transform);
        ChildObject(root, "quality")["registration"] = Stamp(registration, stamp, renamed, carried, carriedMarkers);
        return root.ToJsonString();
    }

    private static void TransformFacets(JsonObject root, RigidTransform3D t)
    {
        foreach (var facet in Facets(root))
        {
            Set(facet, "origin", Vec(facet["origin"]) is { } o ? t.Apply(o) : null, 2);
            Set(facet, "u", Vec(facet["u"]) is { } u ? t.Rotate(u) : null, 6);
            Set(facet, "v", Vec(facet["v"]) is { } v ? t.Rotate(v) : null, 6);
            Set(facet, "normal", Vec(facet["normal"]) is { } n ? t.Rotate(n) : null, 6);
        }
    }

    /// <summary>Cameras map world → camera as <c>x = R·X + t</c>; with <c>X = Rᵀ(X' − T)</c> that is R·Rᵀ and t − R·Rᵀ·T.</summary>
    private static void TransformCameras(JsonObject root, RigidTransform3D transform)
    {
        foreach (var camera in (root["cameras"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (Numbers(camera["R"]) is not { Length: 9 } r || Vec(camera["t"]) is not { } t)
            {
                continue;
            }

            var rotated = new double[9];
            for (var i = 0; i < 3; i++)
            {
                for (var j = 0; j < 3; j++)
                {
                    // (Rc · Rᵀ)[i, j] = Σ_k Rc[i, k] · R[j, k]
                    rotated[(3 * i) + j] = Enumerable.Range(0, 3).Sum(k => r[(3 * i) + k] * transform.Rotation[j, k]);
                }
            }

            var rt = Enumerable.Range(0, 3).Select(i => Enumerable.Range(0, 3).Sum(k => rotated[(3 * i) + k] * transform.Translation[k])).ToArray();
            camera["R"] = Array(rotated, 6);
            camera["t"] = Array(Vec3.Sub(t, rt), 2);
        }
    }

    private static JsonObject Stamp(
        FrameRegistrationResult registration, RegistrationStamp stamp, Dictionary<string, string> renamed, List<string> carried, List<int> carriedMarkers)
    {
        var t = registration.Transform!;
        return new JsonObject
        {
            ["referenceModelId"] = stamp.ReferenceModelId.ToString(),
            ["referencePlanRevision"] = stamp.ReferencePlanRevision,
            ["planRevision"] = stamp.PlanRevision,
            ["usedMarkerIds"] = Ints(registration.UsedIds),
            ["changedMarkerIds"] = Ints(registration.ChangedIds),
            ["outlierMarkerIds"] = Ints(registration.OutlierIds),
            ["rmsMm"] = Math.Round(registration.RmsMm ?? 0, 2),
            ["maxMm"] = Math.Round(registration.MaxMm ?? 0, 2),
            ["rotationDeg"] = Math.Round(t.RotationDeg, 3),
            ["translationMm"] = Math.Round(t.TranslationMm, 1),
            ["renamedFacets"] = new JsonObject(renamed.Where(kv => kv.Key != kv.Value).Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
            ["carriedFacets"] = new JsonArray(carried.Select(f => (JsonNode?)f).ToArray()),
            ["carriedMarkerIds"] = Ints(carriedMarkers),

            // Claimed facets keep the new solve's planes in rebased frames (older models adopted the reference's).
            ["facetFrames"] = "solved-rebased",
        };
    }
}
