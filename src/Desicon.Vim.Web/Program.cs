using Desicon.Vim.Application;
using Desicon.Vim.Infrastructure;
using Desicon.Vim.Infrastructure.Erp;
using Desicon.Vim.Web.Components;
using Desicon.Vim.Web.Endpoints;
using Desicon.Vim.Web.Security;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;

var builder = WebApplication.CreateBuilder(args);

// ---- Sign-in: Microsoft Entra ID (staff only). Roles come from the app registration. ----
builder.Services
    .AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("AzureAd"));
builder.Services.AddControllersWithViews().AddMicrosoftIdentityUI();
builder.Services.AddRazorPages(); // Microsoft.Identity.Web.UI's "signed out" page is a Razor Page

// Send authorisation failures to our own page (anonymous), not the Identity.Web
// one, which the deny-by-default fallback policy would also block (redirect loop).
builder.Services.PostConfigure<CookieAuthenticationOptions>(
    CookieAuthenticationDefaults.AuthenticationScheme,
    o => o.AccessDeniedPath = "/access-denied");
builder.Services.AddAuthorization(o => o.AddVimPolicies());
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<ICurrentActor, CurrentActor>();

// ---- Application and Azure adapters ----
builder.Services.AddVimApplication(builder.Configuration);
builder.Services.AddVimInfrastructure(builder.Configuration);
builder.Services.AddVimHealthChecks();
builder.Services.AddApplicationInsightsTelemetry();

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddAntiforgery();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseVimSecurityHeaders();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets().AllowAnonymous();
app.MapControllers();
app.MapRazorPages();
// Public probe (deployment smoke test, App Service): app, database and storage only.
app.MapHealthChecks("/health", new HealthCheckOptions
{
    Predicate = c => !c.Tags.Contains(BusinessCentralHealthCheck.Tag),
}).AllowAnonymous();

// Business Central connection test, for admins: sign in, then open /health/bc.
app.MapHealthChecks("/health/bc", new HealthCheckOptions
{
    Predicate = c => c.Tags.Contains(BusinessCentralHealthCheck.Tag),
    ResponseWriter = HealthReportWriter.WriteAsync,
}).RequireAuthorization(Policies.Administration);
app.MapVimDocuments();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

await app.Services.SeedVimRoutesAsync().ConfigureAwait(false);
await app.RunAsync().ConfigureAwait(false);
