using GrayMint.Webhooks.Persistence.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace GrayMint.Webhooks.Persistence;

// The module's own tables, in a schema of their own beside the host's. It has no migrations: the setup makes the tables
// from this model (WebhooksSetup), and WebhooksSchema.Version says which shape they have.
internal sealed class WebhooksDbContext(DbContextOptions<WebhooksDbContext> options) : DbContext(options)
{
    // the key of the metadata's one row
    public const int MetadataRowId = 1;

    // the most an attempt's error keeps: a receiver's words can be any length
    public const int MaxErrorLength = 1000;

    // SQL Server keeps no kind with a time, so every time is said to be UTC as it is read, which is how it was written
    private static readonly ValueConverter<DateTime, DateTime> UtcConverter =
        new(x => x, x => DateTime.SpecifyKind(x, DateTimeKind.Utc));

    private static readonly ValueConverter<DateTime?, DateTime?> NullableUtcConverter =
        new(x => x, x => x.HasValue ? DateTime.SpecifyKind(x.Value, DateTimeKind.Utc) : x);

    public DbSet<MessageModel> Messages => Set<MessageModel>();
    public DbSet<AttemptModel> Attempts => Set<AttemptModel>();
    public DbSet<MetadataModel> Metadata => Set<MetadataModel>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var keyCollation = WebhookDialect.KeyCollation(Database);
        modelBuilder.HasDefaultSchema(WebhooksSchema.Name);

        modelBuilder.Entity<MessageModel>(entity => {
            entity.ToTable(WebhooksSchema.MessagesTable);
            entity.HasKey(x => x.MessageId);
            entity.Property(x => x.MessageId).ValueGeneratedOnAdd();
            entity.Property(x => x.SubscriberId).HasMaxLength(WebhookLimits.MaxSubscriberIdLength).UseCollation(keyCollation);
            entity.Property(x => x.Subject).HasMaxLength(WebhookLimits.MaxSubjectLength).UseCollation(keyCollation);
            entity.Property(x => x.EventType).HasMaxLength(WebhookLimits.MaxEventTypeLength);
            entity.Property(x => x.Url).HasMaxLength(WebhookLimits.MaxUrlLength);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(10).IsUnicode(false);
            entity.Property(x => x.LastError).HasMaxLength(MaxErrorLength);

            entity.HasIndex(x => x.Uid).IsUnique();
            // the due messages; a subject's older ones; and a subscriber's, the newest first
            entity.HasIndex(x => new { x.Status, x.NextAttemptTime });
            entity.HasIndex(x => new { x.SubscriberId, x.Subject, x.MessageId });
            entity.HasIndex(x => new { x.SubscriberId, x.MessageId });
        });

        modelBuilder.Entity<AttemptModel>(entity => {
            entity.ToTable(WebhooksSchema.AttemptsTable);
            entity.HasKey(x => x.AttemptId);
            entity.Property(x => x.AttemptId).ValueGeneratedOnAdd();
            entity.Property(x => x.Error).HasMaxLength(MaxErrorLength);
            entity.HasIndex(x => x.MessageId);
            entity.HasOne<MessageModel>()
                .WithMany()
                .HasForeignKey(x => x.MessageId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MetadataModel>(entity => {
            entity.ToTable(WebhooksSchema.MetadataTable);
            entity.HasKey(x => x.MetadataId);
            entity.Property(x => x.MetadataId).ValueGeneratedNever();
        });

        foreach (var property in modelBuilder.Model.GetEntityTypes().SelectMany(x => x.GetProperties())) {
            if (property.ClrType == typeof(DateTime))
                property.SetValueConverter(UtcConverter);
            else if (property.ClrType == typeof(DateTime?))
                property.SetValueConverter(NullableUtcConverter);
        }
    }
}
