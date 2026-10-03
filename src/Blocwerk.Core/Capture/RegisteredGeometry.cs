// <copyright file="RegisteredGeometry.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json;
using System.Text.Json.Nodes;

namespace Blocwerk.Core.Capture;

/// <summary>
/// Reads what a registration wrote into <c>quality.registration</c> of a geometry document: which facets
/// were carried over from the model it was tied to (not re-photographed, so this capture cannot texture them).
/// </summary>
public static class RegisteredGeometry
{
    /// <summary>Where <see cref="DetachFromReference"/> keeps a registration whose reference model is not on this server.</summary>
    public const string HistoryKey = "registrationHistory";

    /// <summary>The model the document was registered to and the facets carried over from it; empty when none.</summary>
    public static (Guid? ReferenceModelId, IReadOnlyList<string> CarriedFacets) Carried(string json)
    {
        try
        {
            var registration = JsonNode.Parse(json)?["quality"]?["registration"];
            if (registration is not JsonObject r)
            {
                return (null, []);
            }

            var id = Guid.TryParse(r["referenceModelId"]?.GetValue<string>(), out var parsed) ? parsed : (Guid?)null;
            var facets = (r["carriedFacets"] as JsonArray ?? []).Select(f => f?.GetValue<string>()).OfType<string>().ToList();
            return (id, facets);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return (null, []);
        }
    }

    /// <summary>
    /// The document no longer tied to the model it was registered to (that model is not on this server): the
    /// registration moves to <c>quality.registrationHistory</c> with <paramref name="reason"/>, so the document reads as
    /// unregistered (its own frame, the first of its lineage) and the history stays readable.
    /// </summary>
    /// <param name="json">The document.</param>
    /// <param name="reason">Why it was detached.</param>
    /// <returns>The document (unchanged when it carries no registration).</returns>
    public static string DetachFromReference(string json, string reason)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        if (root["quality"] is not JsonObject quality || quality["registration"] is not JsonObject registration)
        {
            return json;
        }

        quality.Remove("registration");
        registration["detachedBecause"] = reason;
        quality[HistoryKey] = registration;
        return root.ToJsonString();
    }

    /// <summary>The document without <paramref name="facets"/> (for the textures job).</summary>
    public static string WithoutFacets(string json, IReadOnlyCollection<string> facets)
    {
        if (facets.Count == 0)
        {
            return json;
        }

        var root = JsonNode.Parse(json)!.AsObject();
        foreach (var segment in (root["segments"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (segment["facets"] is not JsonArray list)
            {
                continue;
            }

            foreach (var facet in list.OfType<JsonObject>().Where(f => facets.Contains(f["id"]?.GetValue<string>() ?? string.Empty)).ToList())
            {
                list.Remove(facet);
            }
        }

        return root.ToJsonString();
    }
}
