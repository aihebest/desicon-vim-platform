namespace Desicon.Vim.Domain.Workflow;

/// <summary>
/// Routing configuration (SOP 7.3): which Responsible Officer reviews each
/// invoice category, and who covers for them. Stored in the database and
/// maintained by the Finance Manager — never hard-coded.
/// </summary>
public sealed class CategoryRoute
{
    private CategoryRoute()
    {
    }

    public CategoryRoute(string code, string displayName, string reviewerId, string reviewerName, IEnumerable<string>? delegateIds = null, bool active = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(reviewerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reviewerName);
        Code = code.Trim().ToUpperInvariant();
        DisplayName = displayName;
        ReviewerId = reviewerId;
        ReviewerName = reviewerName;
        DelegateIds = string.Join(',', delegateIds ?? []);
        Active = active;
    }

    public string Code { get; private set; } = string.Empty;

    public string DisplayName { get; private set; } = string.Empty;

    /// <summary>Entra object id of the Responsible Officer.</summary>
    public string ReviewerId { get; private set; } = string.Empty;

    public string ReviewerName { get; private set; } = string.Empty;

    /// <summary>Comma-separated Entra object ids of delegates.</summary>
    public string DelegateIds { get; private set; } = string.Empty;

    public bool Active { get; private set; }

    public IReadOnlyList<string> DelegateIdList =>
        DelegateIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Changes the route and returns the audit record to save with it (null when nothing changed).</summary>
    public RouteChangeRecord? Update(string displayName, string reviewerId, string reviewerName, IEnumerable<string> delegateIds, bool active, Cases.ActionContext ctx)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(reviewerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reviewerName);
        ArgumentNullException.ThrowIfNull(ctx);
        if (!ctx.Actor.HasRole(Cases.VimRole.FinanceManager))
        {
            throw new Cases.DomainRuleException(Cases.RuleCodes.RoleNotPermitted, "Only the Finance Manager can change invoice routing.");
        }

        var before = Snapshot();
        DisplayName = displayName.Trim();
        ReviewerId = reviewerId.Trim();
        ReviewerName = reviewerName.Trim();
        DelegateIds = string.Join(',', delegateIds.Select(d => d.Trim()).Where(d => d.Length > 0));
        Active = active;
        var after = Snapshot();
        return before == after ? null : RouteChangeRecord.Create(Code, before, after, ctx);
    }

    private string Snapshot() => System.Text.Json.JsonSerializer.Serialize(new { DisplayName, ReviewerId, ReviewerName, DelegateIds, Active });

    /// <summary>The five categories in SOP 7.3. Reviewer ids are placeholders until the Entra ids are loaded.</summary>
    public static IReadOnlyList<(string Code, string DisplayName, string RoleTitle)> SopCategories { get; } =
    [
        ("PMC", "PMC project invoices", "PMC Project Secretary"),
        ("GMC", "GMC invoices", "GMC Cost Controller"),
        ("GS", "General Services", "General Services Manager"),
        ("ADMIN_LOGISTICS", "Admin / Logistics", "Logistics Manager"),
        ("SECURITY", "Security services", "Security Manager"),
    ];
}
