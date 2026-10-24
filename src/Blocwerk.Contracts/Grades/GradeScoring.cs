namespace Blocwerk.Core.Helpers;

public static class GradeScoring
{
    public const int FlashBonus = 100;

    public static int GetScore(string? grade, bool isFlash)
    {
        if (string.IsNullOrEmpty(grade))
        {
            return 0;
        }

        var fontGrade = grade;
        if (grade.StartsWith("V"))
        {
            fontGrade = GradeScale.ToFont(grade);
            if (fontGrade == null)
            {
                return 0;
            }
        }

        if (!FontScores.TryGetValue(fontGrade, out var score))
        {
            return 0;
        }

        return isFlash ? score + FlashBonus : score;
    }

    public static string? ScoreToGrade(double score)
    {
        if (score <= 0)
        {
            return null;
        }

        string? closest = null;
        int closestDiff = int.MaxValue;

        foreach (var (grade, gradeScore) in FontScores)
        {
            var diff = Math.Abs(gradeScore - (int)score);
            if (diff < closestDiff)
            {
                closestDiff = diff;
                closest = grade;
            }
        }

        return closest;
    }

    public static IReadOnlyDictionary<string, int> AllScores => FontScores;

    private static readonly Dictionary<string, int> FontScores = new()
    {
        ["2"] = 2000,
        ["2+"] = 2500,
        ["3"] = 3000,
        ["3+"] = 3500,
        ["4"] = 4000,
        ["4+"] = 4500,
        ["5"] = 5000,
        ["5+"] = 5670,
        ["6A"] = 6000,
        ["6A+"] = 6170,
        ["6B"] = 6330,
        ["6B+"] = 6500,
        ["6C"] = 6670,
        ["6C+"] = 6830,
        ["7A"] = 7000,
        ["7A+"] = 7170,
        ["7B"] = 7330,
        ["7B+"] = 7500,
        ["7C"] = 7670,
        ["7C+"] = 7830,
        ["8A"] = 8000,
        ["8A+"] = 8170,
        ["8B"] = 8330,
        ["8B+"] = 8500,
        ["8C"] = 8760,
        ["8C+"] = 8830,
        ["9A"] = 9000,
    };
}
