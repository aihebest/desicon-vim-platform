namespace Desicon.Vim.Domain.Cases;

/// <summary>
/// Thrown when an action would break a business rule (wrong status, wrong
/// role, segregation of duties, unresolved blocking flag...). The
/// <see cref="Code"/> is stable so the UI and tests can rely on it.
/// </summary>
public sealed class DomainRuleException : Exception
{
    public DomainRuleException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public DomainRuleException()
        : this("DOMAIN_RULE", "A business rule was violated.")
    {
    }

    public DomainRuleException(string message)
        : this("DOMAIN_RULE", message)
    {
    }

    public DomainRuleException(string message, Exception innerException)
        : base(message, innerException)
    {
        Code = "DOMAIN_RULE";
    }

    public string Code { get; } = "DOMAIN_RULE";
}

/// <summary>Stable rule codes.</summary>
public static class RuleCodes
{
    public const string ActionNotAllowedInStatus = "ACTION_NOT_ALLOWED_IN_STATUS";
    public const string RoleNotPermitted = "ROLE_NOT_PERMITTED";
    public const string NotAssignedReviewer = "NOT_ASSIGNED_REVIEWER";
    public const string SegregationOfDuties = "SEGREGATION_OF_DUTIES";
    public const string ReasonRequired = "REASON_REQUIRED";
    public const string ChecklistIncomplete = "CHECKLIST_INCOMPLETE";
    public const string ChecklistFailed = "CHECKLIST_FAILED";
    public const string BlockingFlagOpen = "BLOCKING_FLAG_OPEN";
    public const string MissingDetails = "MISSING_DETAILS";
    public const string InvalidValue = "INVALID_VALUE";
}
