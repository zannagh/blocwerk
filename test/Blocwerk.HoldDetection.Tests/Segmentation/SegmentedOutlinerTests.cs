// <copyright file="SegmentedOutlinerTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Configuration;
using Blocwerk.HoldDetection.Tests.Outlines;
using OpenCvSharp;

namespace Blocwerk.HoldDetection.Tests.Segmentation;

/// <summary>
/// The segmentation model on synthetic holds with known masks. Skipped without the model files
/// (models/hold-segmenter-*.onnx, see tools/hold-eval/export_segmenter.py). In the ONNX collection: the detector's
/// library has to create the process's ONNX runtime before any other model does.
/// </summary>
[Collection(OnnxRuntimeCollection.Name)]
public class SegmentedOutlinerTests(OnnxRuntimeFixture onnx)
{
    private static readonly HoldSegmenterSettings Settings = new() { Threads = 2 };

    [Fact]
    public void ModelFilesThatCannotBeLoaded_AreLogged_AndTheHoldIsOutlinedByColour()
    {
        _ = onnx;
        string folder = Directory.CreateTempSubdirectory("segmenter").FullName;
        try
        {
            var settings = new HoldSegmenterSettings
            {
                Threads = 1,
                EncoderPath = Path.Join(folder, "encoder.onnx"),
                DecoderPath = Path.Join(folder, "decoder.onnx"),
            };
            File.WriteAllBytes(settings.EncoderPath, [1, 2, 3, 4]);
            File.WriteAllText(settings.DecoderPath, "not a model");
            var log = new ListLogger();
            using Mat img = SyntheticWall.Wood(600, 500);
            Point[] truth = SyntheticWall.Blob(new Point(300, 240), 80, 60, seed: 11);
            SyntheticWall.PaintHold(img, truth, new Scalar(40, 40, 200), 0);
            Rect box = Cv2.BoundingRect(truth);
            var seed = HoldSeed.FromPixelBox(box.X, box.Y, box.Width, box.Height, img.Width, img.Height);
            var service = HoldDetectionServices.Outliner(settings, null, log);

            using var first = service.OpenSession(img);
            var outline = first.OutlineEach([seed])[0];
            using var second = service.OpenSession(img);
            _ = second.OutlineEach([seed]);

            Assert.Equal(HoldOutlineMethod.Contour, outline.Method);
            Assert.NotNull(outline.ShapePoints);
            Assert.Single(log.Warnings);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [SkippableFact]
    public void TwoTouchingHolds_GetTwoOutlines_AndNeitherSwallowsTheOther()
    {
        Skip.IfNot(ModelsThere(), "Segmenter model files not found");
        _ = onnx;
        using Mat img = SyntheticWall.Wood(900, 700);
        Point[] left = SyntheticWall.Blob(new Point(380, 350), 80, 60, seed: 4);
        Point[] right = SyntheticWall.Blob(new Point(535, 350), 80, 60, seed: 9);
        SyntheticWall.PaintHold(img, left, new Scalar(40, 40, 200), 0);
        SyntheticWall.PaintHold(img, right, new Scalar(40, 200, 230), 0);

        var results = Outline(img, [left, right]);

        Assert.All(results, r => Assert.Equal(HoldOutlineMethod.Segmenter, r.Method));
        Assert.True(IoU(results[0], left, img) >= 0.8, $"left {IoU(results[0], left, img):F2}");
        Assert.True(IoU(results[1], right, img) >= 0.8, $"right {IoU(results[1], right, img):F2}");
        Assert.True(IoU(results[0], right, img) < 0.1, "the left outline bleeds onto the right hold");
    }

    [SkippableFact]
    public void TheSamePhotoTwice_GivesTheSameOutlines()
    {
        Skip.IfNot(ModelsThere(), "Segmenter model files not found");
        using Mat img = SyntheticWall.Wood(900, 700);
        Point[] hold = SyntheticWall.Blob(new Point(450, 350), 90, 70, seed: 2);
        SyntheticWall.PaintHold(img, hold, new Scalar(200, 90, 30), 12);

        var first = Outline(img, [hold])[0];
        var second = Outline(img, [hold])[0];

        Assert.Equal(first.Polygon, second.Polygon);
        Assert.Equal(first.Confidence, second.Confidence);
    }

    [SkippableFact]
    public void APaleHoldOnPaleWood_IsOutlined_WhereTheColourPassGivesUp()
    {
        Skip.IfNot(ModelsThere(), "Segmenter model files not found");
        using Mat img = SyntheticWall.Wood(900, 700);
        Point[] hold = SyntheticWall.Blob(new Point(450, 350), 90, 70, seed: 6);
        SyntheticWall.PaintHold(img, hold, new Scalar(176, 200, 222), 14);

        var result = Outline(img, [hold])[0];

        Assert.Equal(HoldOutlineMethod.Segmenter, result.Method);
        Assert.True(IoU(result, hold, img) >= 0.7, $"IoU {IoU(result, hold, img):F2}");
    }

    private static bool ModelsThere() =>
        File.Exists(HoldDetectionServices.ResolveModelPath(Settings.EncoderPath)) && File.Exists(HoldDetectionServices.ResolveModelPath(Settings.DecoderPath));

    private static IReadOnlyList<HoldOutlineResult> Outline(Mat img, Point[][] holds)
    {
        var seeds = holds.Select(h =>
        {
            Rect box = Cv2.BoundingRect(h);
            return HoldSeed.FromPixelBox(box.X, box.Y, box.Width, box.Height, img.Width, img.Height);
        }).ToList();
        using var session = HoldDetectionServices.Outliner(Settings).OpenSession(img);
        return ((IHoldBatchOutlineSession)session).OutlineAll(seeds);
    }

    private static double IoU(HoldOutlineResult result, Point[] truth, Mat img) =>
        SyntheticWall.IoU(truth, HoldOutlineRealPhotoTests.ToPixels(result.Polygon, img.Width, img.Height), img.Size());
}
