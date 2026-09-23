using YoloDotNet;
using YoloDotNet.Models;

namespace Blocwerk.HoldDetection.Tests;

/// <summary>
/// Serialises every test that constructs a YOLO / ONNX Runtime session, and owns the ONE YOLO instance.
/// YoloDotNet creates ONNX Runtime's process-wide environment with <c>OrtEnv.CreateInstanceWithOptions</c>,
/// which throws "OrtEnv singleton instance already exists" for every later <c>new Yolo(...)</c> in the same
/// process. Test classes used to race for it, so <see cref="YoloDirectTest"/> failed whenever it came second.
/// A collection fixture is built before any test in the collection runs, so it always wins that race.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class OnnxRuntimeCollection : ICollectionFixture<OnnxRuntimeFixture>
{
    /// <summary>The collection name the ONNX-using test classes opt into.</summary>
    public const string Name = "OnnxRuntime";
}

/// <summary>Creates the process's single YOLO instance once, when the model file is available.</summary>
public sealed class OnnxRuntimeFixture : IDisposable
{
    /// <summary>Initializes a new instance of the <see cref="OnnxRuntimeFixture"/> class.</summary>
    public OnnxRuntimeFixture()
    {
        var modelPath = HoldDetectionServices.ResolveModelPath("models/climbingcrux.onnx");
        if (File.Exists(modelPath))
        {
            Options = new YoloOptions { OnnxModel = modelPath };
            Yolo = new Yolo(Options);
        }
    }

    /// <summary>Gets the shared YOLO instance, or null when the model file is missing.</summary>
    public Yolo? Yolo { get; }

    /// <summary>Gets the options the shared instance was created with (YoloDotNet keeps reading them).</summary>
    public YoloOptions? Options { get; }

    /// <inheritdoc/>
    public void Dispose()
    {
        Yolo?.Dispose();
    }
}
