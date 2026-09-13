using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;

namespace Blocwerk.Web.Endpoints;

/// <summary>
/// Development-only <see cref="ICurrentUserService"/> that impersonates a fixed <see cref="User"/>
/// (the wall's owner). The real big-update service resolves its acting user through
/// <see cref="GetCurrentUserAsync"/> and then sets <c>db.CurrentUserId</c> and passes the wall-admin
/// guard from it — the dev endpoints have no auth, so this hands the service the owner so those
/// wall-scoped checks succeed exactly as they would for the signed-in owner in the UI. Only
/// <see cref="GetCurrentUserAsync"/> is ever called by the big-update / panel services; the mutating
/// account members throw, since nothing in this harness touches them.
/// </summary>
internal sealed class DevOwnerCurrentUserService(User owner) : ICurrentUserService
{
    public Task<User> GetCurrentUserAsync() => Task.FromResult(owner);

    public Task<User?> GetUserByIdAsync(Guid id) =>
        Task.FromResult<User?>(owner.Id == id ? owner : null);

    public Task<IReadOnlyList<string>> GetLinkedProvidersAsync() =>
        Task.FromResult<IReadOnlyList<string>>([]);

    public void InvalidateCache()
    {
        // No cache to invalidate — the impersonated user is fixed for the request.
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

    private static NotSupportedException NotSupported() =>
        new("DevOwnerCurrentUserService only supports GetCurrentUserAsync.");
}
