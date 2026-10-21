using AmcTracker.Application.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace AmcTracker.Api.Infrastructure;

/// <summary>
/// There are no roles yet (one staff login), so "admin" means a username listed in
/// Reminders:AdminUsernames. An empty list means nobody is an admin.
/// </summary>
public class AdminOptions
{
    public const string Section = "Reminders";

    public string[] AdminUsernames { get; set; } = [];
}

public class AdminRequirement : IAuthorizationRequirement
{
    public const string Policy = "Admin";
}

public class AdminHandler(IUserRepository users, IOptions<AdminOptions> options) : AuthorizationHandler<AdminRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, AdminRequirement requirement)
    {
        if (!int.TryParse(context.User.FindFirst("sub")?.Value, out var userId)) return;
        var user = await users.GetByIdAsync(userId);
        if (user is { IsActive: true }
            && options.Value.AdminUsernames.Contains(user.Username, StringComparer.OrdinalIgnoreCase))
        {
            context.Succeed(requirement);
        }
    }
}
