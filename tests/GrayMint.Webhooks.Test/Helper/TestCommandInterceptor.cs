using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace GrayMint.Webhooks.Test.Helper;

// What a test does to the module's commands as they go to the database: a wait before one, so something else
// happens first, or a command that fails there.
public sealed class TestCommandInterceptor : DbCommandInterceptor
{
    // runs before each command of the module's context is sent
    public Func<DbCommand, Task>? BeforeCommand { get; set; }

    // the statement that takes a message for a delivery
    public static bool IsClaim(DbCommand command)
    {
        return IsUpdate(command) && !command.CommandText.Contains("CompletedTime", StringComparison.Ordinal);
    }

    // the statement that writes what came of a message's turn
    public static bool IsOutcome(DbCommand command)
    {
        return IsUpdate(command) && command.CommandText.Contains("CompletedTime", StringComparison.Ordinal);
    }

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        await RunBeforeCommand(command);
        return result;
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        await RunBeforeCommand(command);
        return result;
    }

    public override async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        await RunBeforeCommand(command);
        return result;
    }

    // SQL Server's begins with a SET NOCOUNT
    private static bool IsUpdate(DbCommand command)
    {
        return command.CommandText.Contains("UPDATE ", StringComparison.Ordinal);
    }

    private async Task RunBeforeCommand(DbCommand command)
    {
        if (BeforeCommand != null)
            await BeforeCommand(command);
    }
}
