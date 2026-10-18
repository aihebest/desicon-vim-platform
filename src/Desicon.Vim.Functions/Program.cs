using Desicon.Vim.Application;
using Desicon.Vim.Infrastructure;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = FunctionsApplication.CreateBuilder(args);

builder.Services
    .AddApplicationInsightsTelemetryWorkerService()
    .ConfigureFunctionsApplicationInsights();

// By default the Application Insights logger only records Warning and above from the worker.
// Remove that rule so our Information-level counts ("3 emails processed") reach App Insights;
// levels are then controlled by the normal Logging configuration.
builder.Services.Configure<LoggerFilterOptions>(options =>
{
    var defaultRule = options.Rules.FirstOrDefault(rule =>
        rule.ProviderName == "Microsoft.Extensions.Logging.ApplicationInsights.ApplicationInsightsLoggerProvider");
    if (defaultRule is not null)
    {
        options.Rules.Remove(defaultRule);
    }
});

builder.Services.AddVimApplication(builder.Configuration);
builder.Services.AddVimInfrastructure(builder.Configuration);

await builder.Build().RunAsync().ConfigureAwait(false);
