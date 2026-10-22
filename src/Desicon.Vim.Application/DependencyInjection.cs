using Desicon.Vim.Application.Cases;
using Desicon.Vim.Application.Intake;
using Desicon.Vim.Application.Sla;
using Desicon.Vim.Application.Submission;
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
            .Validate(o => o.AccountsCheckWorkingDays > 0 && o.ExceptionReviewWorkingDays > 0, "Vim stage deadlines must be positive.")
            .Validate(o => o.FullValueTolerance >= 0, "Vim:FullValueTolerance cannot be negative.")
            .ValidateOnStart();

        services.AddScoped<IntakeService>();
        services.AddScoped<CaseWorkflowService>();
        services.AddScoped<SlaMonitor>();
        services.AddScoped<DocumentScanSync>();
        services.AddScoped<AutomaticChecks>();
        services.AddScoped<SubmissionService>();
        services.AddScoped<SubmissionProcessor>();
        return services;
    }
}
