// <copyright file="HoldSegmenter.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.HoldDetection.Outlines;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace Blocwerk.HoldDetection.Segmentation;

/// <summary>
/// The box-prompted segmentation model (MobileSAM as two ONNX files, see tools/hold-eval/export_segmenter.py): an
/// encoder that turns a window of the photo into an embedding once, and a light decoder that answers "what is the
/// object in this box" per hold. CPU only, loaded on first use, every call serialised (one photo at a time).
/// </summary>
internal sealed class HoldSegmenter : IDisposable
{
    /// <summary>Side of the encoder's square input.</summary>
    public const int InputSize = 1024;

    /// <summary>Side of the decoder's mask, covering the same square at a quarter of the resolution.</summary>
    public const int MaskSize = 256;

    // The padding colour is the model's own mean, so the padded area is zero after its normalisation.
    private static readonly float[] MeanRgb = [123.675f, 116.28f, 103.53f];

    private readonly string encoderPath;
    private readonly string decoderPath;
    private readonly int threads;
    private readonly Action? beforeLoad;
    private readonly Lock gate = new();
    private InferenceSession? encoder;
    private InferenceSession? decoder;

    /// <summary>Initializes a new instance of the <see cref="HoldSegmenter"/> class.</summary>
    /// <param name="encoderPath">The encoder file.</param>
    /// <param name="decoderPath">The decoder file.</param>
    /// <param name="threads">Threads one inference may use.</param>
    /// <param name="beforeLoad">Runs once before the model is loaded (the detector has to create the ONNX runtime first).</param>
    public HoldSegmenter(string encoderPath, string decoderPath, int threads, Action? beforeLoad = null)
    {
        this.encoderPath = encoderPath;
        this.decoderPath = decoderPath;
        this.threads = Math.Max(1, threads);
        this.beforeLoad = beforeLoad;
    }

    /// <summary>Gets a value indicating whether both model files are there.</summary>
    public bool Available => File.Exists(encoderPath) && File.Exists(decoderPath);

    /// <summary>Encodes one window of the photo.</summary>
    /// <param name="image">The full BGR photo.</param>
    /// <param name="window">The window.</param>
    /// <returns>The embedding and the factor from window pixels to the model's input pixels.</returns>
    public (float[] Embedding, double Scale) Embed(Mat image, Rect window)
    {
        double scale = (double)InputSize / Math.Max(window.Width, window.Height);
        int w = Math.Min(InputSize, (int)Math.Round(window.Width * scale));
        int h = Math.Min(InputSize, (int)Math.Round(window.Height * scale));
        using var view = new Mat(image, window);
        using var resized = new Mat();
        Cv2.Resize(view, resized, new Size(w, h), 0, 0, scale < 1 ? InterpolationFlags.Area : InterpolationFlags.Linear);
        var tensor = new DenseTensor<float>(ToPlanes(CropPixels.ToBytes(resized), w, h), [1, 3, InputSize, InputSize]);
        lock (gate)
        {
            EnsureLoaded();
            using var results = encoder!.Run([NamedOnnxValue.CreateFromTensor("image", tensor)], ["embedding"]);
            return (results.First().AsTensor<float>().ToArray(), scale);
        }
    }

    /// <summary>The mask logits (row-major, <see cref="MaskSize"/> squared) for the object in a box, and the model's own quality score.</summary>
    /// <param name="embedding">The window's embedding.</param>
    /// <param name="box">The box in the model's input pixels (window pixels times the scale).</param>
    /// <returns>The logits (above zero is object) and the score.</returns>
    public (float[] Logits, double Score) Decode(float[] embedding, SegmentBox box)
    {
        var coords = new DenseTensor<float>(
            new[] { (float)box.Left, (float)box.Top, (float)box.Right, (float)box.Bottom, 0f, 0f }, [1, 3, 2]);
        var labels = new DenseTensor<float>(new[] { 2f, 3f, -1f }, [1, 3]);
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("image_embeddings", new DenseTensor<float>(embedding, [1, 256, 64, 64])),
            NamedOnnxValue.CreateFromTensor("point_coords", coords),
            NamedOnnxValue.CreateFromTensor("point_labels", labels),
            NamedOnnxValue.CreateFromTensor("mask_input", new DenseTensor<float>(new float[MaskSize * MaskSize], [1, 1, MaskSize, MaskSize])),
            NamedOnnxValue.CreateFromTensor("has_mask_input", new DenseTensor<float>(new[] { 0f }, [1])),
            NamedOnnxValue.CreateFromTensor("orig_im_size", new DenseTensor<float>(new[] { (float)MaskSize, MaskSize }, [2])),
        };
        lock (gate)
        {
            EnsureLoaded();
            using var results = decoder!.Run(inputs, ["low_res_masks", "iou_predictions"]);
            return (results.First().AsTensor<float>().ToArray(), results.Last().AsTensor<float>().First());
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        encoder?.Dispose();
        decoder?.Dispose();
    }

    /// <summary>Interleaved BGR bytes of a w x h image as RGB planes in the top-left of the padded square input.</summary>
    private static float[] ToPlanes(byte[] bgr, int w, int h)
    {
        const int Plane = InputSize * InputSize;
        var data = new float[3 * Plane];
        for (int c = 0; c < 3; c++)
        {
            Array.Fill(data, MeanRgb[c], c * Plane, Plane);
        }

        for (int y = 0; y < h; y++)
        {
            int row = y * InputSize;
            int source = y * w * 3;
            for (int x = 0; x < w; x++, source += 3)
            {
                data[row + x] = bgr[source + 2];
                data[Plane + row + x] = bgr[source + 1];
                data[(2 * Plane) + row + x] = bgr[source];
            }
        }

        return data;
    }

    private void EnsureLoaded()
    {
        if (encoder is not null)
        {
            return;
        }

        beforeLoad?.Invoke();
        using var options = new SessionOptions { IntraOpNumThreads = threads, InterOpNumThreads = 1 };
        decoder = new InferenceSession(decoderPath, options);
        encoder = new InferenceSession(encoderPath, options);
    }
}
