using Desicon.Vim.Domain.Cases;

namespace Desicon.Vim.Domain.Workflow;

/// <summary>Everything a person (or the system) can do to a case.</summary>
public enum CaseAction
{
    /// <summary>DC confirms details, category and intake checklist.</summary>
    Register,

    /// <summary>Send back to the vendor with a reason (DC at intake, RO at verification).</summary>
    ReturnToVendor,

    /// <summary>Vendor sent corrected documents against the same reference.</summary>
    Resubmit,

    /// <summary>DC routes the case to the Responsible Officer for its category.</summary>
    SendForVerification,

    /// <summary>DC or Finance Manager moves the case to a different reviewer.</summary>
    Reassign,

    /// <summary>Assigned RO confirms the case against the verification checklist.</summary>
    Verify,

    /// <summary>AP Coordinator takes a verified case for final financial check.</summary>
    AcceptForProcessing,

    /// <summary>AP finds an issue that the RO must look at again.</summary>
    SendBackToReviewer,

    /// <summary>AP records the Business Central purchase invoice number.</summary>
    RecordErpEntry,

    /// <summary>Payment confirmed (normally synchronised from Business Central).</summary>
    RecordPayment,

    /// <summary>Case finished.</summary>
    Close,

    /// <summary>Withdrawn, duplicate or raised in error.</summary>
    Cancel,
}

/// <summary>One permitted move. <see cref="To"/> null means the status does not change.</summary>
public sealed record Transition(
    CaseAction Action,
    IReadOnlySet<CaseStatus> From,
    CaseStatus? To,
    IReadOnlySet<VimRole> Roles,
    bool ReasonRequired);

/// <summary>
/// The single table of permitted moves. Every status change goes through
/// <see cref="InvoiceCase.Apply"/>, which consults this table, so there is no
/// second path that could skip an audit event or a role check.
/// </summary>
public static class WorkflowRules
{
    private static Transition T(CaseAction action, CaseStatus[] from, CaseStatus? to, VimRole[] roles, bool reason = false) =>
        new(action, from.ToHashSet(), to, roles.ToHashSet(), reason);

    public static IReadOnlyList<Transition> Transitions { get; } =
    [
        T(CaseAction.Register, [CaseStatus.Received], CaseStatus.Registered, [VimRole.DocumentController]),
        T(CaseAction.ReturnToVendor, [CaseStatus.Received, CaseStatus.Registered], CaseStatus.ReturnedToVendor, [VimRole.DocumentController], reason: true),
        T(CaseAction.ReturnToVendor, [CaseStatus.UnderVerification], CaseStatus.ReturnedToVendor, [VimRole.Reviewer], reason: true),
        T(CaseAction.Resubmit, [CaseStatus.ReturnedToVendor], CaseStatus.Received, [VimRole.System, VimRole.DocumentController]),
        T(CaseAction.SendForVerification, [CaseStatus.Registered], CaseStatus.UnderVerification, [VimRole.DocumentController]),
        T(CaseAction.Reassign, [CaseStatus.UnderVerification], null, [VimRole.DocumentController, VimRole.FinanceManager], reason: true),
        T(CaseAction.Verify, [CaseStatus.UnderVerification], CaseStatus.Verified, [VimRole.Reviewer]),
        T(CaseAction.AcceptForProcessing, [CaseStatus.Verified], CaseStatus.WithAccountsPayable, [VimRole.AccountsPayable]),
        T(CaseAction.SendBackToReviewer, [CaseStatus.WithAccountsPayable], CaseStatus.UnderVerification, [VimRole.AccountsPayable], reason: true),
        T(CaseAction.RecordErpEntry, [CaseStatus.WithAccountsPayable], CaseStatus.EnteredInErp, [VimRole.AccountsPayable]),
        T(CaseAction.RecordPayment, [CaseStatus.EnteredInErp], CaseStatus.Paid, [VimRole.System, VimRole.AccountsPayable]),
        T(CaseAction.Close, [CaseStatus.Paid], CaseStatus.Closed, [VimRole.System, VimRole.AccountsPayable]),
        T(CaseAction.Cancel,
            [CaseStatus.Received, CaseStatus.Registered, CaseStatus.ReturnedToVendor, CaseStatus.UnderVerification, CaseStatus.Verified],
            CaseStatus.Cancelled,
            [VimRole.DocumentController, VimRole.FinanceManager],
            reason: true),
    ];

    /// <summary>Finds the transition that lets this actor do this action now, or throws.</summary>
    public static Transition Resolve(CaseAction action, CaseStatus current, Actor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var candidates = Transitions.Where(t => t.Action == action && t.From.Contains(current)).ToList();
        if (candidates.Count == 0)
        {
            throw new DomainRuleException(RuleCodes.ActionNotAllowedInStatus, $"'{action}' is not allowed while the case is {current}.");
        }

        var match = candidates.FirstOrDefault(t => actor.HasAnyRole(t.Roles));
        return match ?? throw new DomainRuleException(RuleCodes.RoleNotPermitted, $"{actor.DisplayName} does not hold a role permitted to '{action}' a case that is {current}.");
    }

    /// <summary>Actions this actor could attempt now (used by the UI to show buttons).</summary>
    public static IReadOnlyList<CaseAction> AvailableActions(CaseStatus current, Actor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return Transitions
            .Where(t => t.From.Contains(current) && actor.HasAnyRole(t.Roles))
            .Select(t => t.Action)
            .Distinct()
            .ToList();
    }

    public static bool IsTerminal(CaseStatus status) => status is CaseStatus.Closed or CaseStatus.Cancelled;
}
