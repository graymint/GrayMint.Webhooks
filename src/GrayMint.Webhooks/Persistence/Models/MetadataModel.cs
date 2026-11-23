namespace GrayMint.Webhooks.Persistence.Models;

// The one row of what the module keeps about its own tables: the version of their shape (WebhooksSchema.Version).
// The start reads only the columns it knows, so a later column changes nothing for it.
internal sealed class MetadataModel
{
    // always WebhooksDbContext.MetadataRowId
    public required int MetadataId { get; init; }
    public required int SchemaVersion { get; init; }
}
