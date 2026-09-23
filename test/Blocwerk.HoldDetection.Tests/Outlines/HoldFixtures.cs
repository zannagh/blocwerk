using Blocwerk.Core.Abstractions;
using Blocwerk.HoldDetection.Outlines;
using OpenCvSharp;

namespace Blocwerk.HoldDetection.Tests.Outlines;

/// <summary>
/// Real-photo hold crops (JPEG, cut from the owner's wall photos IMG_2770/2780/2783) and hand-picked seed
/// boxes in each crop's pixel coordinates, read off the crops.
/// </summary>
internal static class HoldFixtures
{
    /// <summary>Strongly coloured green hold (IMG_2780).</summary>
    public static readonly HoldFixture Green = new("green_2780", 165, 200, 275, 200);

    /// <summary>White/chalky hold on plain plywood (IMG_2780).</summary>
    public static readonly HoldFixture White = new("white_2780", 235, 205, 200, 185);

    /// <summary>Large wooden volume the colour of the wall (IMG_2783, crop downscaled ×0.5).</summary>
    public static readonly HoldFixture BigVolume = new("volume_2783", 25, 160, 400, 425);

    /// <summary>Small triangular wooden volume, same crop.</summary>
    public static readonly HoldFixture SmallVolume = new("volume_2783", 175, 160, 145, 150);

    /// <summary>Green hold on the overhang with a soft shadow cast below it (IMG_2783).</summary>
    public static readonly HoldFixture ShadowGreen = new("shadow_2783", 228, 182, 102, 58);

    /// <summary>Blue hold with a shadow down-right, same crop.</summary>
    public static readonly HoldFixture ShadowBlue = new("shadow_2783", 78, 150, 117, 93);

    /// <summary>The yellow donut seen from the left in IMG_2770.</summary>
    public static readonly HoldFixture Donut2770 = new("donut_2770", 185, 220, 305, 225);

    /// <summary>The SAME yellow donut in IMG_2783.</summary>
    public static readonly HoldFixture Donut2783 = new("donut_2783", 147, 135, 185, 215);

    /// <summary>A different yellow hold (a ball) in IMG_2783.</summary>
    public static readonly HoldFixture YellowBall = new("yellowball_2783", 100, 102, 145, 140);

    /// <summary>A blue crescent seen from below in IMG_2770.</summary>
    public static readonly HoldFixture Crescent2770 = new("crescent_2770", 100, 110, 115, 65);

    /// <summary>The SAME blue crescent in IMG_2783.</summary>
    public static readonly HoldFixture Crescent2783 = new("crescent_2783", 90, 115, 142, 80);

    /// <summary>All real fixtures.</summary>
    public static readonly HoldFixture[] All =
        [Green, White, BigVolume, SmallVolume, ShadowGreen, ShadowBlue, Donut2770, Donut2783, YellowBall, Crescent2770, Crescent2783];

    /// <summary>Loads a fixture image as BGR.</summary>
    public static Mat Load(string name) => Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Holds", name + ".jpg"), ImreadModes.Color);

    /// <summary>Outlines a fixture's hold.</summary>
    public static (HoldOutlineResult Result, HoldSeed Seed, int Width, int Height) Outline(HoldFixture f)
    {
        using Mat img = Load(f.File);
        HoldSeed seed = HoldSeed.FromPixelBox(f.Left, f.Top, f.Width, f.Height, img.Width, img.Height);
        using IHoldOutlineSession session = new OpenCvHoldOutlineService().OpenSession(img);
        return (session.Outline(seed), seed, img.Width, img.Height);
    }
}
