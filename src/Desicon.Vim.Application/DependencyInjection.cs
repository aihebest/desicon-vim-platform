using Desicon.Vim.Application.Cases;
using Desicon.Vim.Application.Intake;
using Desicon.Vim.Application.Sla;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Desicon.Vim.Application;

public static class DependencyInjection
{
    /// <summary>Registers the use-case services. Infrastructure registers the ports separately.</summary>
    public static IServiceCollection AddVimApplication(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<VimOptions>()
            .Bind(configuration.GetSection(VimOptions.SectionName))
            .Validate(o => Uri.TryCreate(o.WorkbenchUrl, UriKind.Absolute, out _), "Vim:WorkbenchUrl must be an absolute URL.")
            .Validate(o => o.VerificationHours > 0, "Vim:VerificationHours must be positive.")
            .ValidateOnStart();

        services.AddScoped<IntakeService>();
        services.AddScoped<CaseWorkflowService>();
        services.AddScoped<SlaMonitor>();
        services.AddScoped<DocumentScanSync>();
        return services;
    }
}
