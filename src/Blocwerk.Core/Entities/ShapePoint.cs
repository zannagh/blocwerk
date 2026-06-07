namespace Blocwerk.Core.Entities;

public class ShapePoint
{
    public double Dx { get; set; }

    public double Dy { get; set; }

    public static List<ShapePoint> DefaultOctagon(double radius)
    {
        var points = new List<ShapePoint>(8);
        for (int i = 0; i < 8; i++)
        {
            var angle = i * Math.PI / 4.0;
            points.Add(new ShapePoint
            {
                Dx = Math.Round(Math.Cos(angle) * radius, 5),
                Dy = Math.Round(Math.Sin(angle) * radius, 5),
            });
        }

        return points;
    }
}
