using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Desicon.Vim.Infrastructure.Persistence;

/// <summary>
/// Lets <c>dotnet ef</c> build the model without starting the web app.
/// "migrations add" does not connect to a database, so the fallback
/// connection string is only a placeholder. For "database update" pass
/// <c>--connection</c>, or set the environment variable ConnectionStrings__VimDb.
/// </summary>
public sealed class DesignTimeVimDbContextFactory : IDesignTimeDbContextFactory<VimDbContext>
{
    private const string PlaceholderConnection =
        "Server=tcp:localhost,1433;Database=VimDev;Authentication=Active Directory Default;Encrypt=True;TrustServerCertificate=True";

    public VimDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__VimDb");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            connectionString = PlaceholderConnection;
        }

        var options = new DbContextOptionsBuilder<VimDbContext>()
            .UseSqlServer(connectionString, sql => DependencyInjection.ConfigureSql(sql))
            .Options;
        return new VimDbContext(options);
    }
}
