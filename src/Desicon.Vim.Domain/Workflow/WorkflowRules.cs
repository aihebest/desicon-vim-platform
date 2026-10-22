using Desicon.Vim.Domain.Cases;

namespace Desicon.Vim.Domain.Workflow;

/// <summary>Everything a person (or the system) can do to a case (workflow v2).</summary>
public enum CaseAction
{
    /// <summary>System: the automatic checks found problems; rejected with reasons. Accounts or the Finance Manager can also reject.</summary>
    ReturnToVendor,

    /// <summary>The vendor (or AP on their behalf) sent corrected documents against the same reference.</summary>
    Resubmit,

    /// <summary>System: every automatic check passed; the case goes to Accounts.</summary>
    PassToAccounts,

    /// <summary>System: an automatic check needs a decision; the case goes to the Finance Manager.</summary>
    RaiseException,

    /// <summary>Finance Manager: flags cleared, send the case on to Accounts.</summary>
    ReleaseToAccounts,

    /// <summary>Accounts: confirmatory check passed. The vendor is told; credit days start.</summary>
    Accept,

    /// <summary>Accounts records the Business Central purchase invoice number.</summary>
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
        // Automatic checks (system) on a new or resubmitted case.
        T(CaseAction.PassToAccounts, [CaseStatus.Received], CaseStatus.WithAccountsPayable, [VimRole.System]),
        T(CaseAction.RaiseException, [CaseStatus.Received], CaseStatus.ExceptionReview, [VimRole.System]),
        T(CaseAction.ReturnToVendor, [CaseStatus.Received], CaseStatus.ReturnedToVendor, [VimRole.System], reason: true),

        // Finance Manager decides exceptions.
        T(CaseAction.ReleaseToAccounts, [CaseStatus.ExceptionReview], CaseStatus.WithAccountsPayable, [VimRole.FinanceManager]),
        T(CaseAction.ReturnToVendor, [CaseStatus.ExceptionReview], CaseStatus.ReturnedToVendor, [VimRole.FinanceManager], reason: true),

        // Accounts' confirmatory check: accept (official submission) or reject with reasons.
        T(CaseAction.Accept, [CaseStatus.WithAccountsPayable], CaseStatus.Accepted, [VimRole.AccountsPayable]),
        T(CaseAction.ReturnToVendor, [CaseStatus.WithAccountsPayable], CaseStatus.ReturnedToVendor, [VimRole.AccountsPayable], reason: true),

        // Corrected documents start the checks again.
        T(CaseAction.Resubmit, [CaseStatus.ReturnedToVendor], CaseStatus.Received, [VimRole.Vendor, VimRole.AccountsPayable, VimRole.System]),

        // After acceptance (legacy Verified/WithAccountsPayable cases created by workflow v1 end here too).
        T(CaseAction.RecordErpEntry, [CaseStatus.Accepted], CaseStatus.EnteredInErp, [VimRole.AccountsPayable]),
        T(CaseAction.RecordPayment, [CaseStatus.EnteredInErp], CaseStatus.Paid, [VimRole.System, VimRole.AccountsPayable]),
        T(CaseAction.Close, [CaseStatus.Paid], CaseStatus.Closed, [VimRole.System, VimRole.AccountsPayable]),
        T(CaseAction.Cancel,
            [CaseStatus.Received, CaseStatus.ReturnedToVendor, CaseStatus.ExceptionReview, CaseStatus.WithAccountsPayable,
             CaseStatus.Registered, CaseStatus.UnderVerification, CaseStatus.Verified],
            CaseStatus.Cancelled,
            [VimRole.FinanceManager],
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
