// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.RegularExpressions;

namespace Blocwerk.Core.Capture.Scan;

/// <summary>The file list, the mesh, and the cross-check that every file is used and every used file listed.</summary>
public static partial class ScanManifestValidator
{
    private static Dictionary<string, ScanFileEntry> CheckFiles(IReadOnlyList<ScanFileEntry>? files, ScanManifestErrors errors)
    {
        var valid = new Dictionary<string, ScanFileEntry>(StringComparer.Ordinal);
        if (files is not { Count: > 0 } || files.Count > ScanPackageLimits.MaxFiles)
        {
            errors.Add($"files must list between 1 and {ScanPackageLimits.MaxFiles} files.");
            return valid;
        }

        long total = 0;
        foreach (var file in files)
        {
            if (CheckFile(file, errors) && !valid.TryAdd(file.Name!, file))
            {
                errors.Add($"files lists {file.Name} twice.");
            }

            total += Math.Max(0, file.Bytes ?? 0);
        }

        if (total > ScanPackageLimits.MaxTotalBytes)
        {
            errors.Add($"The package is {total / (1024 * 1024)} MB; at most {ScanPackageLimits.MaxTotalBytes / (1024 * 1024)} MB are taken.");
        }

        return valid;
    }

    private static bool CheckFile(ScanFileEntry file, ScanManifestErrors errors)
    {
        if (ScanFileNames.KindOf(file.Name) == ScanFileKind.Invalid)
        {
            errors.Add($"\"{file.Name}\" is not a package file name (video/*.mov|mp4, depth/*.png, confidence/*.png, mesh/*.ply).");
            return false;
        }

        var cap = ScanPackageLimits.MaxBytesOf(file.Name!);
        if (file.Bytes is not { } bytes || bytes <= 0 || bytes > cap)
        {
            errors.Add($"{file.Name}: bytes must be between 1 and {cap} (got {file.Bytes}).");
            return false;
        }

        if (file.Sha256 is null || !Sha256Hex().IsMatch(file.Sha256))
        {
            errors.Add($"{file.Name}: sha256 must be 64 lowercase hex digits.");
            return false;
        }

        return true;
    }

    private static void CheckMesh(ScanMesh? mesh, Dictionary<string, ScanFileEntry> files, ScanManifestErrors errors)
    {
        if (ScanFileNames.KindOf(mesh?.File) != ScanFileKind.Mesh)
        {
            errors.Add("mesh.file must name the ARKit mesh (mesh/*.ply).");
            return;
        }

        RequireListed(mesh!.File!, files, "mesh.file", errors);
    }

    private static void CheckUnreferenced(ScanManifest m, Dictionary<string, ScanFileEntry> files, ScanManifestErrors errors)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var piece in (m.Segments ?? []).SelectMany(s => s.Videos ?? []))
        {
            AddIf(used, piece.File);
        }

        foreach (var depth in m.Depth ?? [])
        {
            AddIf(used, depth.File);
            AddIf(used, depth.ConfidenceFile);
        }

        AddIf(used, m.Mesh?.File);
        foreach (var name in files.Keys.Where(n => !used.Contains(n)))
        {
            errors.Add($"files lists {name}, but nothing in the manifest uses it.");
        }
    }

    private static void RequireListed(string name, Dictionary<string, ScanFileEntry> files, string field, ScanManifestErrors errors)
    {
        if (!files.ContainsKey(name))
        {
            errors.Add($"{field} names {name}, which files does not list.");
        }
    }

    private static void AddIf(HashSet<string> set, string? name)
    {
        if (name is not null)
        {
            set.Add(name);
        }
    }

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Hex();
}
