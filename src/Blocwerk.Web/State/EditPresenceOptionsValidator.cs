// <copyright file="EditPresenceOptionsValidator.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Microsoft.Extensions.Options;

namespace Blocwerk.Web.State;

/// <summary>
/// Refuses, at startup, <see cref="EditPresenceOptions"/> that would quietly switch the deploy gate off or make it
/// meaningless: a 0 or tiny presence window, a cutoff outside the day, an unknown time zone, or a flush that cannot wait.
/// </summary>
/// <remarks>
/// The presence window must outlast <see cref="EditActivityPolicy.LeaseTimeToLive"/> (plus a margin): a circuit that
/// comes back within the lease's lifetime resumes with its unsaved state, and its user must still be "present" then.
/// </remarks>
public sealed class EditPresenceOptionsValidator : IValidateOptions<EditPresenceOptions>
{
    /// <summary>How far the presence window must exceed the lease TTL.</summary>
    public static readonly TimeSpan PresenceMargin = TimeSpan.FromMinutes(1);

    /// <summary>The shortest flush wait worth asking for.</summary>
    public static readonly TimeSpan MinFlushTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The longest flush wait; past it the deploy script's own request would give up first.</summary>
    public static readonly TimeSpan MaxFlushTimeout = TimeSpan.FromSeconds(40);

    public ValidateOptionsResult Validate(string? name, EditPresenceOptions options)
    {
        var failures = new List<string>();
        var minWindow = EditActivityPolicy.LeaseTimeToLive + PresenceMargin;
        if (options.PresenceWindow < minWindow)
        {
            failures.Add($"EditPresence:PresenceWindow must be at least {minWindow} (the lease TTL plus {PresenceMargin}).");
        }

        if (options.CutoffTimeOfDay < TimeSpan.Zero || options.CutoffTimeOfDay >= TimeSpan.FromDays(1))
        {
            failures.Add("EditPresence:CutoffTimeOfDay must be a time of day, 00:00 up to 23:59.");
        }

        if (!ZoneExists(options.CutoffTimeZone))
        {
            failures.Add($"EditPresence:CutoffTimeZone '{options.CutoffTimeZone}' is not a time zone this host knows.");
        }

        if (options.FlushTimeout < MinFlushTimeout || options.FlushTimeout > MaxFlushTimeout)
        {
            failures.Add($"EditPresence:FlushTimeout must be between {MinFlushTimeout} and {MaxFlushTimeout}.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static bool ZoneExists(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }
}
