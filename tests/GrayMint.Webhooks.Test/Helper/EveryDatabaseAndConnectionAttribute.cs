using System.Reflection;

namespace GrayMint.Webhooks.Test.Helper;

// Runs a test on each database, and on each of them both ways a host has the module: on a connection of its own, and
// on the host's own connection, where the host's subscribers read the host's tables between the module's commands.
[AttributeUsage(AttributeTargets.Method)]
public sealed class EveryDatabaseAndConnectionAttribute : Attribute, ITestDataSource
{
    public IEnumerable<object?[]> GetData(MethodInfo methodInfo)
    {
        return Enum.GetValues<TestDatabaseProvider>()
            .SelectMany(x => new[] { new object?[] { x, false }, new object?[] { x, true } });
    }

    public string GetDisplayName(MethodInfo methodInfo, object?[]? data)
    {
        var connection = data?[1] is true ? "the host's connection" : "its own connection";
        return $"{methodInfo.Name} ({data?[0]}, {connection})";
    }
}
