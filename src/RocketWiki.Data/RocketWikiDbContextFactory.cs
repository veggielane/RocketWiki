using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace RocketWiki.Data;

/// <summary>
/// Used only by `dotnet ef migrations add` / `dotnet ef database update` at design
/// time. The API wires up the real connection string via Aspire service discovery at
/// runtime (design.md §15) — this factory exists purely so RocketWiki.Data can produce
/// migrations without depending on RocketWiki.Api.
/// </summary>
public class RocketWikiDbContextFactory : IDesignTimeDbContextFactory<RocketWikiDbContext>
{
    public RocketWikiDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<RocketWikiDbContext>();
        optionsBuilder.UseSqlServer(
            "Server=localhost;Database=RocketWiki;Trusted_Connection=True;TrustServerCertificate=True;");
        return new RocketWikiDbContext(optionsBuilder.Options);
    }
}
