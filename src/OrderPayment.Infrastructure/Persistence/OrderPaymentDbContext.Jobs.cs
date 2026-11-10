using Microsoft.EntityFrameworkCore;
using OrderPayment.Domain;

namespace OrderPayment.Infrastructure.Persistence;

public sealed partial class OrderPaymentDbContext
{
    public DbSet<SyncCheckpoint> SyncCheckpoints => Set<SyncCheckpoint>();
    public DbSet<SyncFailure> SyncFailures => Set<SyncFailure>();

    private static void ConfigureJobs(ModelBuilder modelBuilder)
    {
        var order = modelBuilder.Entity<Order>();
        order.Property(x => x.ExternalMarketplaceId).HasMaxLength(100).UseCollation("Latin1_General_100_BIN2");
        order.HasIndex(x => x.ExternalMarketplaceId).IsUnique().HasFilter("[ExternalMarketplaceId] IS NOT NULL");

        var checkpoint = modelBuilder.Entity<SyncCheckpoint>();
        checkpoint.ToTable("SyncCheckpoints");
        checkpoint.HasKey(x => x.Name);
        checkpoint.Property(x => x.Name).HasMaxLength(100);

        var failure = modelBuilder.Entity<SyncFailure>();
        failure.ToTable("SyncFailures");
        failure.HasKey(x => x.Id);
        failure.Property(x => x.ExternalId).HasMaxLength(100).UseCollation("Latin1_General_100_BIN2").IsRequired();
        failure.HasIndex(x => x.ExternalId).IsUnique();
        failure.Property(x => x.Payload).IsRequired();
        failure.Property(x => x.Error).HasMaxLength(1000).IsRequired();
        failure.HasIndex(x => new { x.ResolvedAt, x.NextAttemptAt });
    }
}
