// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Recognition;
using Blocwerk.Core.Runners;
using Microsoft.Extensions.Configuration;

namespace Blocwerk.Core.Tests;

/// <summary>The instance key: placeholders and low-entropy keys are refused; without one the key file is generated once.</summary>
public sealed class InstanceKeyFileTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"blocwerk-instance-key-{Guid.NewGuid():N}");

    [Theory]
    [InlineData("change-me-to-64-hex-characters-from-openssl-rand-hex-32", "placeholder")]
    [InlineData("CHANGE-ME-0123456789abcdefghijklmnopqrstuvwxyz", "placeholder")]
    [InlineData("abababababababababababababababababababab", "distinct")]
    [InlineData("0123456780123456780123456780123456780123", "distinct")]
    public void APlaceholderOrLowEntropyKeyIsRefused(string key, string why)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Blocwerk:Recognition:InstanceKey"] = key })
            .Build();

        var options = RecognitionQueueOptions.Bind(configuration);

        Assert.Null(options.InstanceKeyHash);
        Assert.Contains(why, options.InstanceKeyRefused, StringComparison.Ordinal);
        Assert.False(GpuRunnerTokens.LooksLikeInstanceKey(key));
    }

    [Fact]
    public void ARandomHexKeyIsAccepted()
    {
        Assert.Null(InstanceKeyPolicy.Refusal(Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))));
    }

    [Fact]
    public void TheKeyFileIsGeneratedOnce_Strong_AndOnlyForItsOwner()
    {
        var path = Path.Combine(root, "recognition", "instance.key");

        var first = InstanceKeyFile.ReadOrCreate(path, owner: null);
        var second = InstanceKeyFile.ReadOrCreate(path, owner: null);

        Assert.Equal(first, second);
        Assert.Equal(64, first.Length);
        Assert.Null(InstanceKeyPolicy.Refusal(first));
        Assert.Single(Directory.EnumerateFiles(Path.GetDirectoryName(path)!));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
    }

    [Fact]
    public void TheKeyFileSettingIsReadAsAFullPath()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Blocwerk:Recognition:InstanceKeyFile"] = Path.Combine(root, "instance.key"),
                ["Blocwerk:Recognition:InstanceKeyFileOwner"] = "1654",
            })
            .Build();

        var options = RecognitionQueueOptions.Bind(configuration);

        Assert.Equal((Path.Combine(root, "instance.key"), 1654), (options.InstanceKeyFile, options.InstanceKeyFileOwner));
        Assert.Null(options.InstanceKeyRefused);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
