using System.Security.Claims;
using Desicon.Vim.Application.VendorAccess;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;

namespace Desicon.Vim.Portal.Security;

/// <summary>Reads the vendor's identity from the External ID sign-in token.</summary>
public static class PortalClaims
{
    private const string ObjectIdClaim = "http://schemas.microsoft.com/identity/claims/objectidentifier";
    private const string TenantIdClaim = "http://schemas.microsoft.com/identity/claims/tenantid";

    /// <summary>"tenantId:objectId" and the sign-in email (verified by External ID's one-time passcode).</summary>
    public static PortalSignIn FromPrincipal(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (user.Identity?.IsAuthenticated != true)
        {
            throw new UnauthorizedAccessException("Not signed in.");
        }

        var oid = user.FindFirstValue(ObjectIdClaim) ?? user.FindFirstValue("oid")
            ?? throw new UnauthorizedAccessException("The sign-in token has no object id.");
        var tid = user.FindFirstValue(TenantIdClaim) ?? user.FindFirstValue("tid")
            ?? throw new UnauthorizedAccessException("The sign-in token has no tenant id.");
        // Only the External ID tenant's own email one-time-passcode accounts are accepted: that is
        // what proves the person controls the email the invitation was sent to. A federated
        // sign-in (Google, a partner's IdP, ...) carries an "idp" claim naming another issuer and
        // its email may never have been verified by us, so it is refused.
        var idp = user.FindFirstValue("idp") ?? user.FindFirstValue("http://schemas.microsoft.com/identity/claims/identityprovider");
        if (!IsOwnEmailAccount(idp, tid))
        {
            throw new UnauthorizedAccessException(
                $"Sign in with your email address and one-time passcode (this sign-in came from '{idp}').");
        }

        // The "email" claim only (optional claim on the app registration). Never
        // preferred_username, which is not a verified address.
        var email = user.FindFirstValue("email") ?? user.FindFirstValue(ClaimTypes.Email)
            ?? throw new UnauthorizedAccessException("The sign-in token has no email address.");
        return new PortalSignIn($"{tid}:{oid}", email);
    }

    /// <summary>
    /// True for the External ID tenant's own email accounts. External ID marks these with no
    /// "idp" claim, with its own tenant/issuer, or with a generic email marker ("mail").
    /// Anything else (google.com, facebook.com, another directory's issuer) is federated.
    /// </summary>
    public static bool IsOwnEmailAccount(string? idp, string tenantId)
    {
        if (string.IsNullOrWhiteSpace(idp))
        {
            return true;
        }

        var value = idp.Trim();
        return value.Contains(tenantId, StringComparison.OrdinalIgnoreCase)
            || value.Contains(".ciamlogin.com", StringComparison.OrdinalIgnoreCase)
            || value.Equals("mail", StringComparison.OrdinalIgnoreCase)
            || value.Equals("email", StringComparison.OrdinalIgnoreCase)
            || value.Equals("localaccount", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Invitation links carry a one-time token in the path. Keep it out of Application
/// Insights so telemetry readers never see a usable link.
/// </summary>
public sealed class RedactInvitationLinks : ITelemetryInitializer
{
    public void Initialize(ITelemetry telemetry)
    {
        ArgumentNullException.ThrowIfNull(telemetry);
        if (telemetry is RequestTelemetry request && request.Url is { } url
            && url.AbsolutePath.StartsWith("/invite/", StringComparison.OrdinalIgnoreCase))
        {
            request.Url = new Uri(url.GetLeftPart(UriPartial.Authority) + "/invite/[redacted]");
            request.Name = "GET /invite/[redacted]";
        }
    }
}

/// <summary>Browser-side protections sent with every response.</summary>
public static class PortalSecurityHeaders
{
    public static IApplicationBuilder UsePortalSecurityHeaders(this IApplicationBuilder app, string? signInAuthority)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Forms post only to this site and to the External ID sign-in host.
        var signInOrigin = Uri.TryCreate(signInAuthority, UriKind.Absolute, out var a) ? a.GetLeftPart(UriPartial.Authority) : string.Empty;
        return app.Use(async (context, next) =>
        {
            var h = context.Response.Headers;
            h["X-Content-Type-Options"] = "nosniff";
            h["X-Frame-Options"] = "DENY";
            h["Referrer-Policy"] = "no-referrer";
            h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
            h["Content-Security-Policy"] =
                "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; " +
                $"frame-ancestors 'none'; form-action 'self' {signInOrigin}; base-uri 'self'";
            await next().ConfigureAwait(false);
        });
    }
}
