namespace GrayMint.Webhooks;

/// <summary>
/// Where the webhooks are kept: tables of their own, in a schema of their own, in a database of their own or beside
/// the host's tables in its database. A host that joins them to its own tables, as for a report, maps them by these
/// names. It reads them, and removes the messages that are done and old enough; it changes nothing else.
/// </summary>
public static class WebhooksSchema
{
    /// <summary>The database schema that holds the tables.</summary>
    public const string Name = "gmwebhook";

    /// <summary>The messages: each webhook queued, with where it stands.</summary>
    public const string MessagesTable = "Messages";

    /// <summary>The attempts: each try to deliver a message, with how it ended.</summary>
    public const string AttemptsTable = "Attempts";

    /// <summary>One row of what the module keeps about its own tables: <c>SchemaVersion</c>, the version of their shape.</summary>
    public const string MetadataTable = "Metadata";

    // The shape the model gives the tables. A change to them raises it, with a step of the setup that takes tables of
    // the version before to it; the start refuses tables of any other version.
    internal const int Version = 1;
}
