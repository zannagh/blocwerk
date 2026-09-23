using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Capture;

/// <summary>
/// The <see cref="ICurrentUserService"/> the capture pipeline hands to the wall services it calls:
/// the admin who started the capture. So the background import goes through exactly the same
/// wall-admin check as the UI would — a user who lost admin rights since cannot activate a model.
/// Only the read members are supported; the pipeline never edits an account.
/// </summary>
internal sealed class CaptureActingUser(User user) : ICurrentUserService
{
    public Task<User> GetCurrentUserAsync() => Task.FromResult(user);

    public Task<User?> GetUserByIdAsync(Guid id) => Task.FromResult<User?>(user.Id == id ? user : null);

    public Task<IReadOnlyList<string>> GetLinkedProvidersAsync() => Task.FromResult<IReadOnlyList<string>>([]);

    public void InvalidateCache()
    {
        // Fixed user for the lifetime of one pipeline step; nothing is cached.
    }

    public Task SetHomeWallAsync(Guid? wallId) => throw NotSupported();

    public Task SetPreferFontGradesAsync(bool preferFont) => throw NotSupported();

    public Task SetShowToolsInNavAsync(bool show) => throw NotSupported();

    public Task SetNotificationDisabledAsync(NotificationType type, bool disabled) => throw NotSupported();

    public Task SetDisplayNameAsync(string? name) => throw NotSupported();

    public Task SetAvatarAsync(byte[]? image, string? contentType) => throw NotSupported();

    public Task SetPasswordAsync(string loginUsername, string password, string? currentPassword) => throw NotSupported();

    public Task<TotpEnrollment> BeginTotpEnrollmentAsync() => throw NotSupported();

    public Task<bool> ConfirmTotpAsync(string code) => throw NotSupported();

    public Task<bool> DisableTotpAsync(string code) => throw NotSupported();

    private static NotSupportedException NotSupported() => new("The capture pipeline never edits an account.");
}
