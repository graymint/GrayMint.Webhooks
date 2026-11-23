using GrayMint.Webhooks.Persistence.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace GrayMint.Webhooks.Persistence;

// Makes the tables when they are not there, any number of times; and refuses a start on a database without them, or
// with tables of another version.
internal static class WebhooksSetup
{
    private const int TableCount = 3;

    // INFORMATION_SCHEMA is on both databases, and lists only the tables the login has a right on. Its names are in
    // upper case, as SQL Server has them: a database whose collation tells case apart wouldn't find them in lower case,
    // and PostgreSQL folds them to lower case either way.
    private const string CountTablesSql =
        $"""
         SELECT CAST(COUNT(*) AS int) AS "Value" FROM INFORMATION_SCHEMA.TABLES
         WHERE TABLE_SCHEMA = '{WebhooksSchema.Name}' AND TABLE_TYPE = 'BASE TABLE'
           AND TABLE_NAME IN ('{WebhooksSchema.MessagesTable}', '{WebhooksSchema.AttemptsTable}',
                              '{WebhooksSchema.MetadataTable}')
         """;

    public static async Task Run(WebhooksDbContext dbContext, HostDatabase? hostDatabase,
        CancellationToken cancellationToken)
    {
        ThrowIfRetrying(dbContext);

        // a database of the module's own is made here; a host's database is the host's to make
        if (hostDatabase == null) {
            var databaseCreator = dbContext.GetService<IRelationalDatabaseCreator>();
            if (!await databaseCreator.ExistsAsync(cancellationToken))
                await databaseCreator.CreateAsync(cancellationToken);
        }

        // the tables and their version are made together, or neither
        await using (var session = await WebhookSession.Begin(dbContext, hostDatabase, isWrite: true,
                         cancellationToken)) {
            var tableCount = await CountTables(dbContext, cancellationToken);
            if (tableCount == 0) {
                await dbContext.GetService<IRelationalDatabaseCreator>().CreateTablesAsync(cancellationToken);
                dbContext.Metadata.Add(new MetadataModel {
                    MetadataId = WebhooksDbContext.MetadataRowId, SchemaVersion = WebhooksSchema.Version
                });

                await dbContext.SaveChangesAsync(cancellationToken);
                dbContext.ChangeTracker.Clear();
            }
            else if (tableCount != TableCount)
                throw new InvalidOperationException(
                    $"Only some of the webhooks' tables are in the schema {WebhooksSchema.Name}, and the setup " +
                    "doesn't know how they came to be so. It changed nothing.");

            await session.Commit(cancellationToken);
        }

        await ThrowIfNotReady(dbContext, hostDatabase, cancellationToken);
    }

    // So a host that never ran the setup stops at its start, not at its first webhook; and so does a build of the
    // module on tables of another shape, which its reads would read wrong.
    public static async Task ThrowIfNotReady(WebhooksDbContext dbContext, HostDatabase? hostDatabase,
        CancellationToken cancellationToken)
    {
        ThrowIfRetrying(dbContext);
        await using var session = await WebhookSession.Begin(dbContext, hostDatabase, isWrite: false,
            cancellationToken);

        if (await CountTables(dbContext, cancellationToken) != TableCount)
            throw new InvalidOperationException(
                $"The webhooks' tables are not in the schema {WebhooksSchema.Name}, or this login has no right on " +
                "them. The setup (UseGrayMintWebhooks) makes them.");

        var versions = await dbContext.Metadata
            .AsNoTracking()
            .Select(x => x.SchemaVersion)
            .ToArrayAsync(cancellationToken);

        if (versions is not [WebhooksSchema.Version])
            throw new InvalidOperationException(
                $"The webhooks' tables in the schema {WebhooksSchema.Name} are of version " +
                $"{(versions.Length == 0 ? "none" : string.Join(" and ", versions))}, and this build of the module " +
                $"works on version {WebhooksSchema.Version}.");
    }

    // A strategy that retries on failure refuses a transaction begun outside it, so every call that writes would
    // fail. A caller retries a whole call instead.
    private static void ThrowIfRetrying(WebhooksDbContext dbContext)
    {
        if (dbContext.Database.CreateExecutionStrategy().RetriesOnFailure)
            throw new InvalidOperationException(
                "The webhooks' context retries on failure (EnableRetryOnFailure), which refuses the transactions its " +
                "calls run in. Leave it out of the module's options, and retry a whole call instead.");
    }

    private static Task<int> CountTables(WebhooksDbContext dbContext, CancellationToken cancellationToken)
    {
        return dbContext.Database.SqlQueryRaw<int>(CountTablesSql).SingleAsync(cancellationToken);
    }
}
