using Azure.AI.DocumentIntelligence;
using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using Desicon.Vim.Application.Abstractions;
using Desicon.Vim.Infrastructure.Documents;
using Desicon.Vim.Infrastructure.Erp;
using Desicon.Vim.Infrastructure.Extraction;
using Desicon.Vim.Infrastructure.Messaging;
using Desicon.Vim.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Graph;

namespace Desicon.Vim.Infrastructure;

public static class DependencyInjection
{
    private static readonly string[] GraphScopes = ["https://graph.microsoft.com/.default"];
    private static readonly string[] ReadyTags = ["ready"];

    /// <summary>
    /// Registers the adapters for every application port. Azure services are
    /// reached with the app's managed identity (DefaultAzureCredential); set
    /// Azure:ManagedIdentityClientId only if a user-assigned identity is used.
    /// </summary>
    public static IServiceCollection AddVimInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<StorageOptions>().Bind(configuration.GetSection(StorageOptions.SectionName));
        services.AddOptions<MailboxOptions>().Bind(configuration.GetSection(MailboxOptions.SectionName));
        services.AddOptions<DocumentIntelligenceOptions>().Bind(configuration.GetSection(DocumentIntelligenceOptions.SectionName));
        services.AddOptions<BusinessCentralOptions>().Bind(configuration.GetSection(BusinessCentralOptions.SectionName));

        // ---- Azure SQL -------------------------------------------------------
        // The connection string is read when the context is first created, so a
        // host that never touches the database (e.g. some tests) does not need it.
        services.AddDbContext<VimDbContext>(o => o.UseSqlServer(
            configuration.GetConnectionString("VimDb")
                ?? throw new InvalidOperationException("ConnectionStrings:VimDb is not configured."),
            ConfigureSql));

        services.AddSingleton<IClock, SystemClock>();
        services.AddScoped<ICaseRepository, EfCaseRepository>();
        services.AddScoped<ICaseQueries, EfCaseQueries>();
        services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        services.AddScoped<IReferenceGenerator, SqlSequenceReferenceGenerator>();
        services.AddScoped<IRouteRepository, EfRouteRepository>();
        services.AddScoped<IVendorAccessRepository, EfVendorAccessRepository>();
        services.AddScoped<IInboundMessageLog, EfInboundMessageLog>();
        services.AddScoped<INotificationOutbox, EfNotificationOutbox>();
        services.AddScoped<RouteSeeder>();

        // ---- Identity for Azure services --------------------------------------
        services.AddSingleton<TokenCredential>(_ =>
        {
            var clientId = configuration["Azure:ManagedIdentityClientId"];
            return new DefaultAzureCredential(new DefaultAzureCredentialOptions
            {
                ManagedIdentityClientId = string.IsNullOrWhiteSpace(clientId) ? null : clientId,
            });
        });

        // ---- Blob Storage -----------------------------------------------------
        services.AddSingleton(sp =>
        {
            var storage = sp.GetRequiredService<IOptions<StorageOptions>>().Value;
            if (storage.UsesConnectionString)
            {
                // Local development with Azurite only.
                return new BlobServiceClient(storage.ConnectionString);
            }

            if (string.IsNullOrWhiteSpace(storage.BlobServiceUri))
            {
                throw new InvalidOperationException("Storage:BlobServiceUri is not configured.");
            }

            return new BlobServiceClient(new Uri(storage.BlobServiceUri), sp.GetRequiredService<TokenCredential>());
        });
        services.AddSingleton<IDocumentStore, BlobDocumentStore>();
        services.AddSingleton<IDocumentScanReader, BlobScanStatusReader>();

        // ---- Document Intelligence (optional) ---------------------------------
        var docIntelEndpoint = configuration[$"{DocumentIntelligenceOptions.SectionName}:Endpoint"];
        if (!string.IsNullOrWhiteSpace(docIntelEndpoint))
        {
            var endpoint = new Uri(docIntelEndpoint);
            services.AddSingleton(sp => new DocumentIntelligenceClient(endpoint, sp.GetRequiredService<TokenCredential>()));
            services.AddScoped<IInvoiceExtractor, DocumentIntelligenceInvoiceExtractor>();
        }
        else
        {
            services.AddSingleton<IInvoiceExtractor, NullInvoiceExtractor>();
        }

        // ---- Business Central (optional, read-only) ---------------------------
        var bc = configuration.GetSection(BusinessCentralOptions.SectionName).Get<BusinessCentralOptions>() ?? new BusinessCentralOptions();
        if (bc.IsEnabled)
        {
            services.AddSingleton<BusinessCentralTokenSource>();
            services.AddSingleton<BusinessCentralCompany>();
            services.AddTransient<BusinessCentralAuthHandler>();
            services.AddHttpClient<IErpGateway, BusinessCentralGateway>(client =>
                {
                    client.BaseAddress = bc.BuildBaseAddress();
                    client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
                })
                .AddHttpMessageHandler<BusinessCentralAuthHandler>()
                .AddStandardResilienceHandler();
        }
        else
        {
            services.AddSingleton<IErpGateway, DisabledErpGateway>();
        }

        // ---- Microsoft Graph (mailbox intake + outbound email) ----------------
        services.AddSingleton(sp => new GraphServiceClient(sp.GetRequiredService<TokenCredential>(), GraphScopes));
        services.AddSingleton<GraphMailboxReader>();
        services.AddScoped<OutboxDispatcher>();

        return services;
    }

    /// <summary>
    /// Health checks: "sql" (can open the database) and "blob" (documents container reachable),
    /// both tagged "ready". Map them in the web host, e.g. app.MapHealthChecks("/health/ready", ...).
    /// </summary>
    public static IHealthChecksBuilder AddVimHealthChecks(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.AddHealthChecks()
            .AddDbContextCheck<VimDbContext>("sql", tags: ReadyTags)
            .AddCheck<BlobStorageHealthCheck>("blob", tags: ReadyTags)
            .AddCheck<BusinessCentralHealthCheck>("business-central", tags: [BusinessCentralHealthCheck.Tag]);
    }

    /// <summary>
    /// Seeds the five SOP category routes if the table is empty. Call once at web startup:
    /// <c>await app.Services.SeedVimRoutesAsync();</c>. It does not apply migrations
    /// (the deployment pipeline does that).
    /// </summary>
    public static async Task SeedVimRoutesAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        var scope = services.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var seeder = scope.ServiceProvider.GetRequiredService<RouteSeeder>();
            try
            {
                await seeder.SeedAsync(ct).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Seeding is best-effort: a database problem must not stop the app starting.
            catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
            {
                // Keep the app up so /health can report the database problem and
                // staff see a clear error, instead of the site crash-looping.
                scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("Desicon.Vim.Startup")
                    .LogError(ex, "Seeding the invoice routes failed at startup; the app will start without them. Check the database connection and permissions.");
            }
        }
    }

    internal static void ConfigureSql(SqlServerDbContextOptionsBuilder sql)
    {
        sql.EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(10), errorNumbersToAdd: null);
        sql.MigrationsAssembly(typeof(VimDbContext).Assembly.GetName().Name);
        sql.MigrationsHistoryTable("__EFMigrationsHistory", VimDbContext.Schema);
    }
}

/// <summary>Checks that the documents container can be reached with the app's identity.</summary>
public sealed class BlobStorageHealthCheck(BlobServiceClient blobService, IOptions<StorageOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var exists = await blobService.GetBlobContainerClient(options.Value.DocumentsContainer)
                .ExistsAsync(cancellationToken)
                .ConfigureAwait(false);
            return exists.Value
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy($"Container '{options.Value.DocumentsContainer}' does not exist.");
        }
        catch (Azure.RequestFailedException ex)
        {
            return HealthCheckResult.Unhealthy("Blob storage is not reachable.", ex);
        }
    }
}
