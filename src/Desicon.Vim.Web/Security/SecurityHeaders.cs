namespace Desicon.Vim.Web.Security;

/// <summary>Browser-side protections sent with every response.</summary>
public static class SecurityHeaders
{
    public static IApplicationBuilder UseVimSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var h = context.Response.Headers;
            h["X-Content-Type-Options"] = "nosniff";
            h["X-Frame-Options"] = "DENY";
            h["Referrer-Policy"] = "strict-origin-when-cross-origin";
            h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";

            // Blazor Server's WebSocket is same-origin, so 'self' covers it. Components use inline styles.
            h["Content-Security-Policy"] =
                "default-src 'self'; " +
                "script-src 'self'; " +
                "style-src 'self' 'unsafe-inline'; " +
                "img-src 'self' data:; " +
                "connect-src 'self'; " +
                "frame-ancestors 'none'; " +
                "form-action 'self' https://login.microsoftonline.com; " +
                "base-uri 'self'";
            await next().ConfigureAwait(false);
        });
}
