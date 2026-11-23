using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace GrayMint.Webhooks.Persistence;

// One call's hold on the database.
// On a host's connection, the host's context opens it, so the host's connection interceptors run as they do for its
// own commands, and the call works in the transaction the host has open: a message queued there commits with the
// host's change, or goes with it.
// A call that writes inside a transaction it did not begin has a savepoint: when it fails, what it wrote is taken
// back and the transaction is left usable, which PostgreSQL would not do after an error. A call that writes with no
// transaction open runs in one of its own.
internal sealed class WebhookSession : IAsyncDisposable
{
    private const string SavepointName = "GrayMintWebhooks";

    private readonly DatabaseFacade? _hostDatabase;
    private readonly IDbContextTransaction? _ownTransaction;
    private readonly IDbContextTransaction? _savepointTransaction;
    private bool _isCommitted;

    private WebhookSession(DatabaseFacade? hostDatabase, IDbContextTransaction? ownTransaction,
        IDbContextTransaction? savepointTransaction)
    {
        _hostDatabase = hostDatabase;
        _ownTransaction = ownTransaction;
        _savepointTransaction = savepointTransaction;
    }

    public static async Task<WebhookSession> Begin(WebhooksDbContext dbContext, HostDatabase? hostDatabase,
        bool isWrite, CancellationToken cancellationToken)
    {
        var database = dbContext.Database;
        if (hostDatabase != null)
            await hostDatabase.Database.OpenConnectionAsync(cancellationToken);

        try {
            if (hostDatabase != null)
                await JoinHostTransaction(database, hostDatabase.Database, cancellationToken);

            if (!isWrite)
                return new WebhookSession(hostDatabase?.Database, null, null);

            if (database.CurrentTransaction == null)
                return new WebhookSession(hostDatabase?.Database,
                    await database.BeginTransactionAsync(cancellationToken), null);

            await database.CurrentTransaction.CreateSavepointAsync(SavepointName, cancellationToken);
            return new WebhookSession(hostDatabase?.Database, null, database.CurrentTransaction);
        }
        catch {
            if (hostDatabase != null)
                await hostDatabase.Database.CloseConnectionAsync();
            throw;
        }
    }

    // The module's context holds on to a host's transaction it joined until it is told otherwise, after the host has
    // ended it too.
    private static async Task JoinHostTransaction(DatabaseFacade database, DatabaseFacade hostDatabase,
        CancellationToken cancellationToken)
    {
        var hostTransaction = hostDatabase.CurrentTransaction?.GetDbTransaction();
        if (database.CurrentTransaction?.GetDbTransaction() == hostTransaction)
            return;

        if (database.CurrentTransaction != null)
            await database.UseTransactionAsync(null, cancellationToken);
        if (hostTransaction != null)
            await database.UseTransactionAsync(hostTransaction, cancellationToken);
    }

    // Whether a commit of this call is the end of its transaction: what it wrote is there for every other connection
    // from then on. Inside a transaction of the host's it is not, and only the host knows when that one ends.
    public bool IsOwnTransaction => _ownTransaction != null;

    public async Task Commit(CancellationToken cancellationToken)
    {
        if (_ownTransaction != null)
            await _ownTransaction.CommitAsync(cancellationToken);
        if (_savepointTransaction != null)
            await _savepointTransaction.ReleaseSavepointAsync(SavepointName, cancellationToken);
        _isCommitted = true;
    }

    // not cancellable: what a failed call wrote must be taken back
    public async ValueTask DisposeAsync()
    {
        try {
            // On SQL Server a deadlock, or an error with XACT_ABORT on, ends the whole transaction, the host's with
            // it: its connection is gone from it, and nothing is left to take back.
            if (_ownTransaction != null)
                await _ownTransaction.DisposeAsync();
            else if (_savepointTransaction != null && !_isCommitted &&
                     _savepointTransaction.GetDbTransaction().Connection != null)
                await _savepointTransaction.RollbackToSavepointAsync(SavepointName, CancellationToken.None);
        }
        finally {
            if (_hostDatabase != null)
                await _hostDatabase.CloseConnectionAsync();
        }
    }
}
