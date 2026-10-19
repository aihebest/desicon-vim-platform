using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Desicon.Vim.Web.Endpoints;

/// <summary>Writes a health report as readable JSON (status and description per check).</summary>
public static class HealthReportWriter
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(report);
        context.Response.ContentType = "application/json; charset=utf-8";
        var body = new
        {
            status = report.Status.ToString(),
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                description = e.Value.Description,
                durationMs = (int)e.Value.Duration.TotalMilliseconds,
            }),
        };
        return context.Response.WriteAsync(JsonSerializer.Serialize(body, Options));
    }
}
