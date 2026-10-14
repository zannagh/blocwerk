// <copyright file="Program.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json;
using Blocwerk.HoldEval;

// Usage: Blocwerk.HoldEval <model.onnx> <out-dir> <photo.jpg>... [--key=value ...]
//   writes <out-dir>/<photo name>.json per photo: every detection with what each stage made of it.
// Usage: Blocwerk.HoldEval <model.onnx> <out-dir> --redetect=<wall id> --db=<connection string of a COPY>
//   plans the app's re-detect for every live panel of the wall and writes <out-dir>/<panel id>.json. Applies nothing.
var options = args.Where(a => a.StartsWith("--", StringComparison.Ordinal))
    .Select(a => a[2..].Split('=', 2))
    .ToDictionary(p => p[0], p => p.Length > 1 ? p[1] : "true");
var positional = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
if (positional.Count == 2 && options.TryGetValue("redetect", out var wall) && options.TryGetValue("db", out var connection))
{
    Directory.CreateDirectory(positional[1]);
    await RedetectRun.RunAsync(positional[0], positional[1], connection, Guid.Parse(wall));
    return 0;
}

if (positional.Count < 3)
{
    Console.Error.WriteLine("usage: Blocwerk.HoldEval <model.onnx> <out-dir> <photo>... [--seed=circle|box] [--markers=false]");
    return 2;
}

var settings = EvalSettings.From(options);
Directory.CreateDirectory(positional[1]);
using var pipeline = new EvalPipeline(positional[0], settings);
foreach (var photo in positional.Skip(2))
{
    var run = await pipeline.RunAsync(await File.ReadAllBytesAsync(photo));
    var target = Path.Join(positional[1], Path.GetFileNameWithoutExtension(photo) + ".json");
    await File.WriteAllTextAsync(target, JsonSerializer.Serialize(run, EvalJson.Options));
    Console.WriteLine($"{Path.GetFileName(photo)}: {run.Holds.Count} holds, detect {run.DetectMs:F0} ms, outline {run.OutlineMs:F0} ms -> {target}");
}

return 0;
