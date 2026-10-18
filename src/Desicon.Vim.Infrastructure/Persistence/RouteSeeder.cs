using Desicon.Vim.Domain.Workflow;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Desicon.Vim.Infrastructure.Persistence;

/// <summary>
/// First-run setup of the five SOP routing categories. Runs only when the
/// CategoryRoutes table is empty; after that, routes are maintained by the
/// Finance Manager in the workbench and configuration is ignored.
/// Reviewer ids come from configuration, e.g.
/// <c>Vim:Routes:PMC:ReviewerId</c> (Entra object id) and <c>Vim:Routes:PMC:ReviewerName</c>.
/// </summary>
public sealed class RouteSeeder(VimDbContext db, IConfiguration configuration, ILogger<RouteSeeder> logger)
{
    public async Task<int> SeedAsync(CancellationToken ct)
    {
        if (await db.CategoryRoutes.AnyAsync(ct).ConfigureAwait(false))
        {
            return 0;
        }

        var added = 0;
        foreach (var (code, displayName, roleTitle) in CategoryRoute.SopCategories)
        {
            var section = configuration.GetSection($"Vim:Routes:{code}");
            var reviewerId = section["ReviewerId"];
            if (string.IsNullOrWhiteSpace(reviewerId))
            {
                logger.LogWarning(
                    "Route {Code} ({RoleTitle}) was not seeded: set Vim:Routes:<code>:ReviewerId to the reviewer's Entra object id.",
                    code,
                    roleTitle);
                continue;
            }

            var reviewerName = section["ReviewerName"];
            db.CategoryRoutes.Add(new CategoryRoute(
                code,
                displayName,
                reviewerId.Trim(),
                string.IsNullOrWhiteSpace(reviewerName) ? roleTitle : reviewerName.Trim()));
            added++;
        }

        if (added > 0)
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            logger.LogInformation("Seeded {Count} category route(s).", added);
        }

        return added;
    }
}
