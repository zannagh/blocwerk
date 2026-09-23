using System.Runtime.InteropServices;
using OpenCvSharp;

namespace Blocwerk.HoldDetection.Outlines;

/// <summary>
/// Managed per-pixel colour planes of a working crop: CIE Lab in real units (L* 0..100, a*/b* centred on 0,
/// converted from OpenCV's 8-bit Lab exactly as the overlap matcher's samples are) and rg-chromaticity,
/// which is what tells a shadow on the wall (same chromaticity, darker) from a hold.
/// </summary>
internal sealed class CropPixels
{
    private CropPixels(int width, int height)
    {
        Width = width;
        Height = height;
        L = new float[width * height];
        A = new float[width * height];
        B = new float[width * height];
        R = new float[width * height];
        G = new float[width * height];
    }

    /// <summary>Gets the crop width.</summary>
    public int Width { get; }

    /// <summary>Gets the crop height.</summary>
    public int Height { get; }

    /// <summary>Gets L* per pixel (0..100).</summary>
    public float[] L { get; }

    /// <summary>Gets a* per pixel (0 = neutral).</summary>
    public float[] A { get; }

    /// <summary>Gets b* per pixel (0 = neutral).</summary>
    public float[] B { get; }

    /// <summary>Gets the red chromaticity R/(R+G+B) per pixel.</summary>
    public float[] R { get; }

    /// <summary>Gets the green chromaticity G/(R+G+B) per pixel.</summary>
    public float[] G { get; }

    /// <summary>Builds the planes from a BGR crop after a light 3×3 median (chalk speckle, JPEG noise).</summary>
    /// <param name="bgr">The working crop.</param>
    /// <returns>The planes.</returns>
    public static CropPixels From(Mat bgr)
    {
        using var smooth = new Mat();
        Cv2.MedianBlur(bgr, smooth, 3);
        using var lab = new Mat();
        Cv2.CvtColor(smooth, lab, ColorConversionCodes.BGR2Lab);
        byte[] bgrBytes = ToBytes(smooth);
        byte[] labBytes = ToBytes(lab);

        var px = new CropPixels(bgr.Width, bgr.Height);
        for (int i = 0; i < px.L.Length; i++)
        {
            int o = i * 3;
            px.L[i] = labBytes[o] * (100f / 255f);
            px.A[i] = labBytes[o + 1] - 128f;
            px.B[i] = labBytes[o + 2] - 128f;
            float sum = bgrBytes[o] + bgrBytes[o + 1] + bgrBytes[o + 2] + 1f;
            px.R[i] = bgrBytes[o + 2] / sum;
            px.G[i] = bgrBytes[o + 1] / sum;
        }

        return px;
    }

    /// <summary>Copies a Mat's pixels into a managed array (continuous copy when needed).</summary>
    /// <param name="mat">Any 8-bit Mat.</param>
    /// <returns>The raw bytes, row-major.</returns>
    public static byte[] ToBytes(Mat mat)
    {
        using Mat? copy = mat.IsContinuous() ? null : mat.Clone();
        Mat src = copy ?? mat;
        var data = new byte[src.Total() * src.ElemSize()];
        Marshal.Copy(src.Data, data, 0, data.Length);
        return data;
    }

    /// <summary>Creates a single-channel 8-bit Mat from a managed mask.</summary>
    /// <param name="data">Row-major mask bytes.</param>
    /// <param name="width">Mask width.</param>
    /// <param name="height">Mask height.</param>
    /// <returns>A new Mat owning a copy of the data.</returns>
    public static Mat ToMat(byte[] data, int width, int height)
    {
        var mat = new Mat(height, width, MatType.CV_8UC1);
        Marshal.Copy(data, 0, mat.Data, data.Length);
        return mat;
    }
}
