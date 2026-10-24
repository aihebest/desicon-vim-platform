using Desicon.Vim.Application;
using Desicon.Vim.Infrastructure;
using Desicon.Vim.Portal.Security;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;

var builder = WebApplication.CreateBuilder(args);

// ---- Sign-in: Microsoft Entra External ID (vendors). Staff use the workbench, never this app. ----
builder.Services
    .AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("AzureAd"));
builder.Services.AddControllersWithViews().AddMicrosoftIdentityUI();

// Deny by default: every page needs a signed-in vendor unless it opts out ([AllowAnonymous]).
builder.Services.AddAuthorization(o => o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
builder.Services.AddRazorPages();

// ---- Application and Azure adapters (same database and rules as the workbench) ----
builder.Services.AddVimApplication(builder.Configuration);
builder.Services.AddVimInfrastructure(builder.Configuration);
builder.Services.AddVimHealthChecks();
builder.Services.AddApplicationInsightsTelemetry();
builder.Services.AddSingleton<ITelemetryInitializer, RedactInvitationLinks>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UsePortalSecurityHeaders(builder.Configuration["AzureAd:Authority"]);
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapRazorPages();
app.MapHealthChecks("/health", new HealthCheckOptions
{
    // Database only: the portal has no document-storage access in phase 1, and Business
    // Central is checked separately by the workbench.
    Predicate = c => c.Name == "sql",
}).AllowAnonymous();

await app.RunAsync().ConfigureAwait(false);
