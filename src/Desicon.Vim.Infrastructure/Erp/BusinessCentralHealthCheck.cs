using Desicon.Vim.Application.Abstractions;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Desicon.Vim.Infrastructure.Erp;

/// <summary>
/// Proves the Business Central connection end to end: token (managed identity ->
/// connector -> BC tenant), environment, company lookup and read permission.
/// Served on its own admin-only endpoint (/health/bc), never in the public /health
/// probe, so a Business Central outage cannot fail a deployment or restart the app.
/// </summary>
public sealed class BusinessCentralHealthCheck(IErpGateway erp) : IHealthCheck
{
    public const string Tag = "business-central";

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (erp is not BusinessCentralGateway bc)
        {
            return HealthCheckResult.Degraded("Business Central checks are switched off (no connector client ID configured).");
        }

        try
        {
            var result = await bc.TestConnectionAsync(cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy(result);
        }
#pragma warning disable CA1031 // A health check reports every failure rather than throwing.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            return HealthCheckResult.Unhealthy("Could not read Business Central: " + ex.Message);
        }
    }
}
