using Desicon.Vim.Domain.Cases;

namespace Desicon.Vim.Domain.Checklists;

/// <summary>When in the process a checklist item is answered.</summary>
public enum ChecklistStage
{
    /// <summary>Legacy (workflow v1): Document Controller, at registration.</summary>
    Intake = 0,

    /// <summary>Legacy (workflow v1): Responsible Officer, at verification.</summary>
    Verification = 1,

    /// <summary>Accounts' confirmatory check before acceptance (workflow v2).</summary>
    AccountsCheck = 2,
}

/// <summary>Which packages an item applies to.</summary>
public enum AppliesTo
{
    All = 0,
    MaterialsOnly = 1,
    ServicesOnly = 2,
}

public enum ChecklistAnswer
{
    Unanswered = 0,
    Yes = 1,
    No = 2,
    NotApplicable = 3,
}

/// <summary>One line of the Desicon Invoice Review Checklist.</summary>
/// <param name="Code">Stable identifier stored with every answer.</param>
/// <param name="Section">Heading as it appears on the Finance checklist.</param>
/// <param name="Text">The check, in Finance's words.</param>
/// <param name="Mandatory">A "No" on a mandatory item blocks progress — the case must be returned instead.</param>
/// <param name="AllowNotApplicable">Whether "NA" is an acceptable answer (e.g. timesheets "where applicable").</param>
public sealed record ChecklistItem(
    string Code,
    ChecklistStage Stage,
    string Section,
    string Text,
    AppliesTo AppliesTo,
    bool Mandatory = true,
    bool AllowNotApplicable = false)
{
    public bool AppliesToSupply(SupplyType supply) => AppliesTo switch
    {
        AppliesTo.All => true,
        AppliesTo.MaterialsOnly => supply == SupplyType.Materials,
        AppliesTo.ServicesOnly => supply == SupplyType.Services,
        _ => false,
    };
}

/// <summary>An answer recorded against a checklist item.</summary>
public sealed record ChecklistResponse(string ItemCode, ChecklistAnswer Answer, string? Remark = null);

/// <summary>
/// A versioned checklist. The definition is configuration: Finance can revise
/// it (new version) without code changes; each case records the version it
/// was checked against.
/// </summary>
public sealed class ChecklistDefinition
{
    public ChecklistDefinition(string version, IReadOnlyList<ChecklistItem> items)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentNullException.ThrowIfNull(items);

        var duplicates = items.GroupBy(i => i.Code, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicates.Count > 0)
        {
            throw new ArgumentException($"Duplicate checklist codes: {string.Join(", ", duplicates)}", nameof(items));
        }

        Version = version;
        Items = items;
    }

    public string Version { get; }

    public IReadOnlyList<ChecklistItem> Items { get; }

    public IReadOnlyList<ChecklistItem> ItemsFor(ChecklistStage stage, SupplyType supply) =>
        Items.Where(i => i.Stage == stage && i.AppliesToSupply(supply)).ToList();

    /// <summary>
    /// Checks a set of answers for one stage. Returns problems; an empty list
    /// means the stage is complete and passed.
    /// </summary>
    public ChecklistEvaluation Evaluate(ChecklistStage stage, SupplyType supply, IReadOnlyCollection<ChecklistResponse> responses)
    {
        ArgumentNullException.ThrowIfNull(responses);
        var byCode = responses.ToDictionary(r => r.ItemCode, StringComparer.Ordinal);
        var unanswered = new List<string>();
        var failed = new List<string>();

        foreach (var item in ItemsFor(stage, supply))
        {
            if (!byCode.TryGetValue(item.Code, out var response) || response.Answer == ChecklistAnswer.Unanswered)
            {
                unanswered.Add(item.Code);
                continue;
            }

            var isFailure = response.Answer == ChecklistAnswer.No
                || (response.Answer == ChecklistAnswer.NotApplicable && !item.AllowNotApplicable);

            if (isFailure && item.Mandatory)
            {
                failed.Add(item.Code);
            }
        }

        return new ChecklistEvaluation(unanswered, failed);
    }

    /// <summary>
    /// The items answered "No" (or "NA" where NA is not allowed) for a stage, with the
    /// checker's remark: what the vendor is told was not met when an invoice is returned.
    /// </summary>
    public IReadOnlyList<ChecklistShortfall> NotMet(ChecklistStage stage, SupplyType supply, IReadOnlyCollection<ChecklistResponse> responses)
    {
        ArgumentNullException.ThrowIfNull(responses);
        var byCode = responses.GroupBy(r => r.ItemCode, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        var result = new List<ChecklistShortfall>();
        foreach (var item in ItemsFor(stage, supply))
        {
            if (byCode.TryGetValue(item.Code, out var r)
                && (r.Answer == ChecklistAnswer.No || (r.Answer == ChecklistAnswer.NotApplicable && !item.AllowNotApplicable)))
            {
                result.Add(new ChecklistShortfall(item, string.IsNullOrWhiteSpace(r.Remark) ? null : r.Remark.Trim()));
            }
        }

        return result;
    }

    /// <summary>Plain-text reason for the vendor: the items not met, then the checker's comment.</summary>
    public static string DescribeReturn(IReadOnlyList<ChecklistShortfall> notMet, string? comment)
    {
        ArgumentNullException.ThrowIfNull(notMet);
        var note = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        if (notMet.Count == 0)
        {
            return note ?? string.Empty;
        }

        var lines = new List<string> { "Not met on the Accounts checklist:" };
        lines.AddRange(notMet.Select(n => n.Remark is null ? $"- {n.Item.Text}" : $"- {n.Item.Text} ({n.Remark})"));
        if (note is not null)
        {
            lines.Add("Comment from Accounts: " + note);
        }

        return string.Join("\n", lines);
    }
}

/// <summary>A checklist item that was not met, with the checker's remark.</summary>
public sealed record ChecklistShortfall(ChecklistItem Item, string? Remark);

public sealed record ChecklistEvaluation(IReadOnlyList<string> Unanswered, IReadOnlyList<string> Failed)
{
    public bool IsComplete => Unanswered.Count == 0;

    public bool Passed => IsComplete && Failed.Count == 0;
}
