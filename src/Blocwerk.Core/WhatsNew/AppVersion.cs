// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Reflection;

namespace Blocwerk.Core.WhatsNew;

/// <summary>The running build's version, as GitVersion stamped it into every assembly (e.g. <c>0.1.1-224</c>).</summary>
public static class AppVersion
{
    /// <summary>The version without the <c>+sha</c> build metadata; <c>dev</c> when nothing was stamped.</summary>
    public static string Current { get; } = Resolve();

    private static string Resolve()
    {
        var info = typeof(AppVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(info))
        {
            return "dev";
        }

        var plus = info.IndexOf('+');
        return plus > 0 ? info[..plus] : info;
    }
}
