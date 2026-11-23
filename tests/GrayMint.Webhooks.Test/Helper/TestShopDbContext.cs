using Microsoft.EntityFrameworkCore;

namespace GrayMint.Webhooks.Test.Helper;

// A host's own context, with a table of its own: the webhooks' tables go beside it, on its connection.
public sealed class TestShopDbContext(DbContextOptions<TestShopDbContext> options) : DbContext(options)
{
    public DbSet<TestOrder> Orders => Set<TestOrder>();
}
