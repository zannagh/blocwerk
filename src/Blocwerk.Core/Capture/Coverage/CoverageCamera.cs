// <copyright file="CoverageCamera.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json.Nodes;
using Blocwerk.Core.Geometry.Footprints;

namespace Blocwerk.Core.Capture.Coverage;

/// <summary>
/// A posed camera of the capture for the coverage report: a solved photo from the model's <c>cameras</c>, or a
/// registered walk-along video frame when the photo-real stage reports its pose (<c>frame.json</c>
/// <see cref="VideoCamerasField"/>: the same shape as the model's cameras, in wall-world millimetres).
/// </summary>
public sealed class CoverageCamera
{
    /// <summary>The optional <c>frame.json</c> array with the registered video frames' poses.</summary>
    public const string VideoCamerasField = "videoCameras";

    /// <summary>A projected point this close to the image border (share of the width) does not count as in view.</summary>
    private const double BorderShare = 0.01;

    /// <summary>Initializes a new instance of the <see cref="CoverageCamera"/> class.</summary>
    /// <param name="camera">The solved camera.</param>
    /// <param name="isVideoFrame">True for a video frame.</param>
    public CoverageCamera(SolvedCamera camera, bool isVideoFrame)
    {
        Camera = camera;
        IsVideoFrame = isVideoFrame;
        Centre = camera.Centre;
        Forward = [camera.R[6], camera.R[7], camera.R[8]];
        FocalPx = Math.Max(1, (Math.Abs(camera.K[0]) + Math.Abs(camera.K[4])) / 2);
    }

    /// <summary>The solved camera.</summary>
    public SolvedCamera Camera { get; }

    /// <summary>True for a registered video frame, false for a capture photo.</summary>
    public bool IsVideoFrame { get; }

    /// <summary>The camera centre, world mm.</summary>
    public double[] Centre { get; }

    /// <summary>The viewing direction (optical axis), world, unit.</summary>
    public double[] Forward { get; }

    /// <summary>The focal length, px.</summary>
    public double FocalPx { get; }

    /// <summary>The photos' cameras of a geometry model JSON.</summary>
    /// <param name="modelJson">The model JSON.</param>
    /// <returns>The cameras.</returns>
    public static IReadOnlyList<CoverageCamera> FromModel(string modelJson) =>
        SolvedCamera.ParseAll(modelJson).Where(Usable).Select(c => new CoverageCamera(c, false)).ToList();

    /// <summary>The registered video frames' cameras of a photo-real <c>frame.json</c>; empty when it has none.</summary>
    /// <param name="frameJson">The frame JSON, or null.</param>
    /// <returns>The cameras.</returns>
    public static IReadOnlyList<CoverageCamera> FromSplatFrame(string? frameJson)
    {
        if (string.IsNullOrWhiteSpace(frameJson))
        {
            return [];
        }

        try
        {
            if (JsonNode.Parse(frameJson)?[VideoCamerasField] is not JsonArray cameras)
            {
                return [];
            }

            var wrapped = new JsonObject { ["cameras"] = cameras.DeepClone() }.ToJsonString();
            return SolvedCamera.ParseAll(wrapped).Where(Usable).Select(c => new CoverageCamera(c, true)).ToList();
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    /// <summary>Whether the point projects into the image (not behind the camera, not on the very border).</summary>
    /// <param name="world">World point, mm.</param>
    /// <returns>True when in view.</returns>
    public bool InFrame(double[] world)
    {
        if (Camera.Project(world) is not { } px)
        {
            return false;
        }

        var margin = Camera.Width * BorderShare;
        return px.X >= margin && px.Y >= margin && px.X <= Camera.Width - margin && px.Y <= Camera.Height - margin;
    }

    private static bool Usable(SolvedCamera c) =>
        c.Width > 0 && c.Height > 0 && c.K[0] > 0 && c.K.Concat(c.R).Concat(c.T).All(double.IsFinite);
}
