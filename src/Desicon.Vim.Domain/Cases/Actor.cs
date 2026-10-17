namespace Desicon.Vim.Domain.Cases;

/// <summary>
/// The person (or the system) performing an action. <see cref="Id"/> is the
/// Microsoft Entra object id, so every decision is tied to a real identity.
/// </summary>
public sealed record Actor(string Id, string DisplayName, IReadOnlySet<VimRole> Roles)
{
    public static readonly Actor System = new("system", "VIM Platform", new HashSet<VimRole> { VimRole.System });

    public bool HasRole(VimRole role) => Roles.Contains(role);

    public bool HasAnyRole(IEnumerable<VimRole> roles) => roles.Any(Roles.Contains);

    public static Actor Create(string id, string displayName, params VimRole[] roles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        return new Actor(id, displayName, roles.ToHashSet());
    }
}
