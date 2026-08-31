using Microsoft.AspNetCore.Authorization;

namespace Blocwerk.Authentication.Authorization;

/// <summary>
/// Marker for the app-wide administration area. Satisfied only by a signed-in user whose
/// <see cref="Blocwerk.Core.Entities.User.Role"/> is <see cref="Blocwerk.Core.Enums.IdentityRole.Admin"/>.
/// The Admin role is not emitted as a claim, so it must be resolved from the database rather than the
/// principal — see <see cref="AppAdminHandler"/>.
/// </summary>
public sealed class AppAdminRequirement : IAuthorizationRequirement;
