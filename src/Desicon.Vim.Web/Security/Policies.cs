using System.Security.Claims;
using Desicon.Vim.Domain.Cases;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;

namespace Desicon.Vim.Web.Security;

/// <summary>
/// Maps Entra app roles ("Vim.DocumentController" ...) to policies. Screens
/// check policies; the domain re-checks every rule, so hiding a button is a
/// convenience, never the control.
/// </summary>
public static class Policies
{
    public const string AnyVimUser = nameof(AnyVimUser);
    public const string DocumentController = nameof(DocumentController);
    public const string Reviewer = nameof(Reviewer);
    public const string AccountsPayable = nameof(AccountsPayable);
    public const string FinanceManager = nameof(FinanceManager);
    public const string Administration = nameof(Administration);

    public const string RolePrefix = "Vim.";

    public static string RoleName(VimRole role) => RolePrefix + role;

    public static AuthorizationOptions AddVimPolicies(this AuthorizationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var all = Enum.GetValues<VimRole>().Where(r => r != VimRole.System).Select(RoleName).ToArray();
        options.AddPolicy(AnyVimUser, p => p.RequireAuthenticatedUser().RequireRole(all));
        options.AddPolicy(DocumentController, p => p.RequireRole(RoleName(VimRole.DocumentController)));
        options.AddPolicy(Reviewer, p => p.RequireRole(RoleName(VimRole.Reviewer)));
        options.AddPolicy(AccountsPayable, p => p.RequireRole(RoleName(VimRole.AccountsPayable)));
        options.AddPolicy(FinanceManager, p => p.RequireRole(RoleName(VimRole.FinanceManager)));
        options.AddPolicy(Administration, p => p.RequireRole(RoleName(VimRole.FinanceManager), RoleName(VimRole.PlatformAdmin)));

        // Deny by default: every page needs a Vim role unless it opts out.
        options.FallbackPolicy = options.GetPolicy(AnyVimUser);
        return options;
    }
}

/// <summary>Turns the signed-in user into a domain <see cref="Actor"/>.</summary>
public interface ICurrentActor
{
    Task<Actor> GetAsync();
}

public sealed class CurrentActor(AuthenticationStateProvider authState) : ICurrentActor
{
    private const string ObjectIdClaim = "http://schemas.microsoft.com/identity/claims/objectidentifier";

    public async Task<Actor> GetAsync()
    {
        var user = (await authState.GetAuthenticationStateAsync().ConfigureAwait(false)).User;
        return FromPrincipal(user);
    }

    public static Actor FromPrincipal(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (user.Identity?.IsAuthenticated != true)
        {
            throw new UnauthorizedAccessException("Not signed in.");
        }

        // The Entra object id is stable for a person; the UPN/email can change.
        var id = user.FindFirstValue(ObjectIdClaim) ?? user.FindFirstValue("oid")
            ?? throw new UnauthorizedAccessException("The sign-in token has no object id.");
        var name = user.FindFirstValue("name") ?? user.Identity.Name ?? id;

        var roles = new HashSet<VimRole>();
        foreach (var role in Enum.GetValues<VimRole>().Where(r => r != VimRole.System))
        {
            if (user.IsInRole(Policies.RoleName(role)))
            {
                roles.Add(role);
            }
        }

        return new Actor(id, name, roles);
    }
}
