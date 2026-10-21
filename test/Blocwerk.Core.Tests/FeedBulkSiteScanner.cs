// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.RegularExpressions;
using Blocwerk.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Finds every <c>ExecuteUpdate</c>/<c>ExecuteDelete</c> call in the app's sources and the entity it writes: the
/// <see cref="BlocwerkDbContext"/> set its statement starts from, or, for a call on a local query, the set (or the
/// <c>IQueryable&lt;T&gt;</c>) that local was declared from. A call whose entity cannot be read this way is reported
/// with a null entity, and has to be classified as such.
/// </summary>
internal static partial class FeedBulkSiteScanner
{
    private static readonly HashSet<string> Keywords = ["await", "var", "return", "new", "this", "base", "using", "if", "else"];

    /// <summary>The call sites, one per call, with their file relative to <c>src/</c>.</summary>
    public static IReadOnlyList<FeedBulkSiteCall> Scan()
    {
        var sets = Sets();
        var types = sets.Values.ToDictionary(t => t.Name, t => t);
        var src = Path.Combine(RepoRoot(), "src");
        var calls = new List<FeedBulkSiteCall>();
        foreach (var path in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(src, path).Replace('\\', '/');
            if (relative.Contains("/Migrations/") || relative.Contains("/obj/") || relative.Contains("/bin/"))
            {
                continue;
            }

            calls.AddRange(ScanText(relative, File.ReadAllText(path), sets, types));
        }

        return calls;
    }

    /// <summary>The calls in one source text, for the scanner's own tests.</summary>
    public static IReadOnlyList<FeedBulkSiteCall> ScanText(string file, string source)
    {
        var sets = Sets();
        return ScanText(file, source, sets, sets.Values.ToDictionary(t => t.Name, t => t));
    }

    /// <summary>The repository root (the directory holding <c>Blocwerk.slnx</c>).</summary>
    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Blocwerk.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Blocwerk.slnx not found above the test binaries.");
    }

    private static List<FeedBulkSiteCall> ScanText(string file, string source, Dictionary<string, Type> sets, Dictionary<string, Type> types)
    {
        var text = StripComments(source);
        return ExecuteCall().Matches(text).Select(call => new FeedBulkSiteCall(file, Resolve(text, call.Index, sets, types))).ToList();
    }

    private static Dictionary<string, Type> Sets() =>
        typeof(BlocwerkDbContext).GetProperties()
            .Where(p => p.PropertyType.IsGenericType && p.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>))
            .ToDictionary(p => p.Name, p => p.PropertyType.GetGenericArguments()[0]);

    private static Type? Resolve(string text, int at, Dictionary<string, Type> sets, Dictionary<string, Type> types)
    {
        var statement = Statement(text, at);
        if (SetOf(statement, sets, types) is { } direct)
        {
            return direct;
        }

        // A local query: the set or the IQueryable<T> it was declared from, in the nearest declaration above the call.
        foreach (var root in Roots(statement))
        {
            var before = text[..at];
            var declared = Regex.Matches(before, $@"\b{Regex.Escape(root)}\s*=(?![=>])").LastOrDefault();
            if (declared is not null && SetOf(Statement(text, Math.Min(text.IndexOf(';', declared.Index), at)), sets, types) is { } local)
            {
                return local;
            }

            var typed = Regex.Matches(before, $@"IQueryable<(\w+)>\s+{Regex.Escape(root)}\b").LastOrDefault();
            if (typed is not null && types.TryGetValue(typed.Groups[1].Value, out var type))
            {
                return type;
            }
        }

        return null;
    }

    private static Type? SetOf(string statement, Dictionary<string, Type> sets, Dictionary<string, Type> types)
    {
        foreach (Match m in SetReference().Matches(statement))
        {
            if (m.Groups["set"].Success && sets.TryGetValue(m.Groups["set"].Value, out var set))
            {
                return set;
            }

            if (m.Groups["type"].Success && types.TryGetValue(m.Groups["type"].Value, out var type))
            {
                return type;
            }
        }

        return null;
    }

    // The receiver identifiers of the statement: names not reached through a member access, followed by '.', '(' or the call.
    private static IEnumerable<string> Roots(string statement) =>
        RootIdentifier().Matches(statement).Select(m => m.Groups[1].Value).Where(r => !Keywords.Contains(r)).Distinct();

    // The text of the statement or argument the call at `at` belongs to: back to the nearest ';', '{', '}' or ',' outside
    // parentheses, or to the '(' that opens the argument list it sits in.
    private static string Statement(string text, int at)
    {
        var depth = 0;
        var i = at - 1;
        for (; i >= 0; i--)
        {
            var c = text[i];
            if (c == ')')
            {
                depth++;
            }
            else if (c == '(' && --depth < 0)
            {
                break;
            }
            else if (depth == 0 && c is ';' or '{' or '}' or ',')
            {
                break;
            }
        }

        return text[(i + 1)..at];
    }

    private static string StripComments(string text) => LineComment().Replace(text, string.Empty);

    [GeneratedRegex(@"\.Execute(?:Update|Delete)(?:Async)?\s*\(")]
    private static partial Regex ExecuteCall();

    [GeneratedRegex(@"\.(?<set>[A-Z]\w*)\b(?!\s*[(<])|\bSet<(?<type>\w+)>\s*\(")]
    private static partial Regex SetReference();

    [GeneratedRegex(@"(?<![\.\w])([a-z]\w*)(?=\s*[\.(]|\s*$)")]
    private static partial Regex RootIdentifier();

    [GeneratedRegex(@"(?m)^\s*///?.*$")]
    private static partial Regex LineComment();
}
