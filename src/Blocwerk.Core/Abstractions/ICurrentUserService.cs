using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Abstractions;

public interface ICurrentUserService
{
    Task<User> GetCurrentUserAsync();
}
