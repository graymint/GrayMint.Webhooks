using Microsoft.EntityFrameworkCore.Infrastructure;

namespace GrayMint.Webhooks.Persistence;

// What the two databases the webhooks run on say differently. Everything else goes through EF, which says it for each.
internal static class WebhookDialect
{
    private const string PostgreSqlProvider = "Npgsql.EntityFrameworkCore.PostgreSQL";
    private const string SqlServerProvider = "Microsoft.EntityFrameworkCore.SqlServer";

    // How a subscriber's id and a subject compare, or null for the database's own way. One differs from another by
    // any character, its case too: SQL Server's own collation is mostly blind to case, and would hand one
    // subscriber's messages to another whose id differs by it.
    public static string? KeyCollation(DatabaseFacade database)
    {
        return database.ProviderName switch {
            PostgreSqlProvider => null,
            SqlServerProvider => "Latin1_General_100_BIN2",
            _ => throw new NotSupportedException(
                $"The webhooks run on PostgreSQL and SQL Server, and their context was given {database.ProviderName}.")
        };
    }
}
