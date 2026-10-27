// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Helpers;

public static class GradeScale
{
    public static readonly (string V, string Font)[] Grades =
    [
        ("VB", "3"),
        ("V0-", "3+"),
        ("V0", "4"),
        ("V0+", "4+"),
        ("V1", "5"),
        ("V2", "5+"),
        ("V2", "6A"),
        ("V3", "6A+"),
        ("V4-", "6B"),
        ("V4+", "6B+"),
        ("V5", "6C"),
        ("V5+", "6C+"),
        ("V6", "7A"),
        ("V7", "7A+"),
        ("V8", "7B"),
        ("V8+", "7B+"),
        ("V9", "7C"),
        ("V10", "7C+"),
        ("V11", "8A"),
        ("V12", "8A+"),
        ("V13", "8B"),
        ("V14", "8B+"),
        ("V15", "8C"),
    ];

    public static string[] VGrades => Grades.Select(g => g.V).Distinct().ToArray();

    public static string[] FontGrades => Grades.Select(g => g.Font).Distinct().ToArray();

    public static string? ToFont(string vGrade)
    {
        return Grades.FirstOrDefault(g => g.V == vGrade).Font;
    }

    public static string? ToV(string fontGrade)
    {
        return Grades.FirstOrDefault(g => g.Font == fontGrade).V;
    }

    /// <summary>
    /// Which scale a stored grade is written in: true for Fontainebleau, false for the V-Scale, null when it is
    /// empty or in neither (the two scales share no grade string).
    /// </summary>
    public static bool? IsFont(string? grade)
    {
        if (string.IsNullOrWhiteSpace(grade))
        {
            return null;
        }

        if (FontGrades.Contains(grade))
        {
            return true;
        }

        return VGrades.Contains(grade) ? false : null;
    }

    public static string Display(string grade, bool useFont)
    {
        if (useFont)
        {
            var font = ToFont(grade);
            return font ?? grade;
        }

        var v = ToV(grade);
        return v ?? grade;
    }
}
