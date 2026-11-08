// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.IO.Compression;
using System.Text.Json;
using Blocwerk.Core.Helpers;
using Blocwerk.Recognition.Worker.Jobs;

namespace Blocwerk.Recognition.Worker.Envelope;

/// <summary>
/// A recognition job's input: the runner envelope (protocol §2.1, contracts <c>envelope/parts</c>) with
/// <c>service: "recognition"</c>, a <c>request</c> JSON part and the kind's image parts. Anything off the contract is a
/// <see cref="RecognitionInputException"/>. Images are size-checked from their headers before anyone decodes them.
/// </summary>
public sealed class RecognitionEnvelope
{
    public const string PartsName = "parts.json";

    private const string RequestPart = "request";

    private static readonly Dictionary<string, Dictionary<string, (int Min, int Max)>> FileParts = new()
    {
        ["detect-holds@2"] = new() { ["photos"] = (1, 8) },
        ["outline-holds"] = new() { ["photo"] = (1, 1) },
        ["markers"] = new() { ["photo"] = (1, 1) },
        ["match-panels"] = new() { ["left"] = (1, 1), ["right"] = (1, 1) },
        ["presence"] = new() { ["old"] = (1, 1), ["new"] = (1, 1) },
        ["texture-match"] = new() { ["photo"] = (1, 1), ["texture"] = (1, 1), ["mask"] = (0, 1) },
    };

    private readonly IReadOnlyList<(string Name, string File)> parts;
    private readonly Dictionary<string, byte[]> files;

    private RecognitionEnvelope(string kind, JsonElement request, IReadOnlyList<(string Name, string File)> parts, Dictionary<string, byte[]> files)
    {
        Kind = kind;
        RequestJson = request;
        this.parts = parts;
        this.files = files;
    }

    public string Kind { get; }

    public JsonElement RequestJson { get; }

    /// <summary>Gets the largest pixel count among the images read so far.</summary>
    public long MaxPixels { get; private set; }

    public static RecognitionEnvelope Read(byte[] bundle, string kind)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(bundle), ZipArchiveMode.Read);
            using var stream = (zip.GetEntry(PartsName) ?? throw Invalid("it has no parts.json")).Open();
            using var document = JsonDocument.Parse(stream);
            var (request, fileParts) = Validate(document.RootElement, kind);
            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var (_, file) in fileParts)
            {
                using var entry = (zip.GetEntry(file) ?? throw Invalid($"the file of part '{file}' is missing")).Open();
                using var buffer = new MemoryStream();
                entry.CopyTo(buffer);
                files[file] = buffer.ToArray();
            }

            return new RecognitionEnvelope(kind, request.Clone(), fileParts, files);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException)
        {
            throw Invalid(ex.Message);
        }
    }

    /// <summary>Checks <c>parts.json</c> against the contract for <paramref name="kind"/>.</summary>
    /// <returns>The request part and the file parts in order.</returns>
    public static (JsonElement Request, List<(string Name, string File)> Files) Validate(JsonElement root, string kind)
    {
        if (!Is(root, "version", 1) || !Is(root, "service", "recognition") || !Is(root, "kind", kind) || !FileParts.TryGetValue(kind, out var rules)
            || !root.TryGetProperty("parts", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            throw Invalid($"it is not a version 1 recognition envelope for '{kind}'");
        }

        JsonElement? request = null;
        var fileParts = new List<(string Name, string File)>();
        foreach (var part in list.EnumerateArray())
        {
            var name = part.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : throw Invalid("a part has no name");
            var hasFile = part.TryGetProperty("file", out var file) && file.ValueKind == JsonValueKind.String;
            var hasJson = part.TryGetProperty("json", out var json);
            if (name == RequestPart && hasJson && !hasFile && request is null)
            {
                request = json;
            }
            else if (rules.ContainsKey(name) && hasFile && !hasJson && IsPlainName(file.GetString()!))
            {
                fileParts.Add((name, file.GetString()!));
            }
            else
            {
                throw Invalid($"part '{name}' is not one '{kind}' takes");
            }
        }

        foreach (var (name, (min, max)) in rules)
        {
            var count = fileParts.Count(p => p.Name == name);
            if (count < min || count > max)
            {
                throw Invalid($"'{kind}' takes {min}..{max} '{name}' parts, not {count}");
            }
        }

        return (request ?? throw Invalid("it has no request part"), fileParts);
    }

    /// <summary>The request part, after checking its <c>version</c> is 1.</summary>
    /// <typeparam name="T">The kind's request record.</typeparam>
    /// <returns>The request.</returns>
    public T Request<T>()
    {
        if (!Is(RequestJson, "version", 1))
        {
            throw Invalid("the request is not version 1");
        }

        try
        {
            return RequestJson.Deserialize<T>(WorkerJson.Options) ?? throw Invalid("the request is empty");
        }
        catch (JsonException ex)
        {
            throw Invalid($"the request does not parse: {ex.Message}");
        }
    }

    /// <summary>The images of a part, refused above <see cref="ImagePixelLimit.MaxPixels"/> before any decode.</summary>
    public IReadOnlyList<(string File, byte[] Bytes)> Images(string name)
    {
        var images = parts.Where(p => p.Name == name).Select(p => (p.File, files[p.File])).ToList();
        foreach (var (file, bytes) in images)
        {
            var (width, height) = ImagePixelLimit.ReadSize(bytes) ?? throw Invalid($"'{file}' is not a readable image");
            if (ImagePixelLimit.IsTooLarge(width, height))
            {
                throw new RecognitionInputException(
                    $"Photo '{file}' has more than {ImagePixelLimit.MaxPixels / 1_000_000} megapixels.", Protocol.RunnerFailure.Resource);
            }

            MaxPixels = Math.Max(MaxPixels, (long)width * height);
        }

        return images;
    }

    public byte[] Image(string name) => Images(name).Single().Bytes;

    private static bool Is(JsonElement element, string property, int value) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var p)
        && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var v) && v == value;

    private static bool Is(JsonElement element, string property, string value) =>
        element.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.String && p.GetString() == value;

    private static bool IsPlainName(string name) =>
        name.Length is > 0 and <= 255 && name is not "." and not ".." && name != PartsName
        && !name.Any(c => c is '/' or '\\' or ':' || char.IsControl(c));

    private static RecognitionInputException Invalid(string why) => new($"The job's input envelope is invalid: {why}.");
}
