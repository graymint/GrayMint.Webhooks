using System.Reflection;

namespace GrayMint.Webhooks.Test.Helper;

// Runs a test once on each database the tables run on.
[AttributeUsage(AttributeTargets.Method)]
public sealed class EveryDatabaseAttribute : Attribute, ITestDataSource
{
    public IEnumerable<object?[]> GetData(MethodInfo methodInfo)
    {
        return Enum.GetValues<TestDatabaseProvider>().Select(x => new object?[] { x });
    }

    public string GetDisplayName(MethodInfo methodInfo, object?[]? data)
    {
        return $"{methodInfo.Name} ({data?[0]})";
    }
}
