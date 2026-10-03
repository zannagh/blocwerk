// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.Components.Pages.Walls;

/// <summary>
/// The ready-to-copy commands that start a 3D runner. The key never appears in them: it is read from
/// <c>runner.env</c> (<c>--env-file</c>), so it stays out of shell history and <c>ps</c>. The CUDA image is built from a
/// checkout for now (not published); the Brush image serves AMD/Intel GPUs.
/// </summary>
public static class GpuRunnerCommands
{
    public const string CudaImage = "blocwerk-splat-worker-cuda:local";
    public const string BrushImage = "ghcr.io/zannagh/blocwerk-splat-worker:latest";
    public const string EnvFile = "runner.env";

    /// <summary>The one line of <c>runner.env</c>.</summary>
    public static string EnvLine(string key) => $"BWR_KEY={key}";

    /// <summary>NVIDIA: build the CUDA image once (from a Blocwerk checkout), then run it with the env file.</summary>
    public static string Cuda(string server) =>
        $"docker build -f docker/splat-worker/Dockerfile.cuda -t {CudaImage} .\n"
        + $"docker run -d --name blocwerk-runner --restart unless-stopped --gpus all --env-file {EnvFile} {CudaImage} "
        + $"python -m splatworker.gpurunner --server {server}";

    /// <summary>AMD / Intel on Linux: the published Brush image with the GPU device.</summary>
    public static string Vulkan(string server) =>
        $"docker run -d --name blocwerk-runner --restart unless-stopped --device /dev/dri --env-file {EnvFile} {BrushImage} "
        + $"python -m splatworker.gpurunner --server {server}";

    /// <summary>Apple Silicon, natively (Docker on a Mac has no GPU).</summary>
    public static string Native(string server) =>
        $"set -a; . ./{EnvFile}; set +a; caffeinate -i docker/splat-worker/run-runner-native.sh --server {server}";
}
