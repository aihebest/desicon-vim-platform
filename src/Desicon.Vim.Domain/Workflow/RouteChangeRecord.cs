using Desicon.Vim.Domain.Cases;

namespace Desicon.Vim.Domain.Workflow;

/// <summary>Insert-only record of a routing change: who changed which reviewer, when, from what to what.</summary>
public sealed class RouteChangeRecord
{
    private RouteChangeRecord()
    {
    }

    public Guid Id { get; private set; }

    public string RouteCode { get; private set; } = string.Empty;

    public string ChangedById { get; private set; } = string.Empty;

    public string ChangedByName { get; private set; } = string.Empty;

    public DateTimeOffset ChangedAtUtc { get; private set; }

    public string OldValueJson { get; private set; } = string.Empty;

    public string NewValueJson { get; private set; } = string.Empty;

    internal static RouteChangeRecord Create(string code, string before, string after, ActionContext ctx) => new()
    {
        Id = Guid.CreateVersion7(ctx.NowUtc),
        RouteCode = code,
        ChangedById = ctx.Actor.Id,
        ChangedByName = ctx.Actor.DisplayName,
        ChangedAtUtc = ctx.NowUtc,
        OldValueJson = before,
        NewValueJson = after,
    };
}
