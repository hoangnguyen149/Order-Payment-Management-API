using Microsoft.EntityFrameworkCore;
using OrderPayment.Domain;

namespace OrderPayment.Infrastructure.Persistence;

public sealed partial class OrderPaymentDbContext
{
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<PaymentEvent> PaymentEvents => Set<PaymentEvent>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<OutboxDelivery> OutboxDeliveries => Set<OutboxDelivery>();

    private static void ConfigurePayments(ModelBuilder modelBuilder)
    {
        var payment = modelBuilder.Entity<Payment>();
        payment.ToTable("Payments", table => table.HasCheckConstraint("CK_Payments_Amount", "[Amount] > 0"));
        payment.HasKey(x => x.Id);
        payment.Property(x => x.PaymentReference).HasMaxLength(64).UseCollation("Latin1_General_100_BIN2").IsRequired();
        payment.HasAlternateKey(x => x.PaymentReference);
        payment.Property(x => x.IdempotencyKey).HasMaxLength(100).UseCollation("Latin1_General_100_BIN2").IsRequired();
        // Một giao dịch/đơn là giới hạn có chủ đích của demo; DB bảo vệ cả race từ nhiều instance.
        payment.HasIndex(x => x.OrderId).IsUnique();
        payment.HasIndex(x => new { x.OrderId, x.IdempotencyKey }).IsUnique();
        payment.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        payment.Property(x => x.Amount).HasPrecision(18, 2);
        payment.Property(x => x.RowVersion).IsRowVersion();
        payment.HasOne<Order>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
        payment.HasIndex(x => new { x.Status, x.CreatedAt }).IncludeProperties(x => new { x.OrderId, x.LastCheckedAt });

        var paymentEvent = modelBuilder.Entity<PaymentEvent>();
        paymentEvent.ToTable("PaymentEvents", table =>
        {
            table.HasCheckConstraint("CK_PaymentEvents_Status", "[Status] IN (N'Succeeded', N'Failed')");
            table.HasCheckConstraint("CK_PaymentEvents_Outcome", "[Outcome] IN (N'Processed', N'Ignored')");
        });
        paymentEvent.HasKey(x => x.Id);
        paymentEvent.Property(x => x.EventId).HasMaxLength(128).UseCollation("Latin1_General_100_BIN2").IsRequired();
        paymentEvent.HasIndex(x => x.EventId).IsUnique();
        paymentEvent.Property(x => x.PaymentReference).HasMaxLength(64).UseCollation("Latin1_General_100_BIN2").IsRequired();
        paymentEvent.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        paymentEvent.Property(x => x.Amount).HasPrecision(18, 2);
        paymentEvent.Property(x => x.Outcome).HasMaxLength(20).IsRequired();
        paymentEvent.HasOne<Payment>().WithMany().HasForeignKey(x => x.PaymentReference)
            .HasPrincipalKey(x => x.PaymentReference).OnDelete(DeleteBehavior.Restrict);

        var outbox = modelBuilder.Entity<OutboxMessage>();
        outbox.ToTable("OutboxMessages");
        outbox.HasKey(x => x.Id);
        outbox.Property(x => x.Type).HasMaxLength(100).IsRequired();
        outbox.Property(x => x.Payload).IsRequired();
        outbox.Property(x => x.LastError).HasMaxLength(1000);
        outbox.HasIndex(x => new { x.ProcessedAt, x.NextAttemptAt, x.CreatedAt }).HasFilter("[ProcessedAt] IS NULL");

        var delivery = modelBuilder.Entity<OutboxDelivery>();
        delivery.ToTable("OutboxDeliveries");
        delivery.HasKey(x => x.MessageId);
        delivery.Property(x => x.MessageId).ValueGeneratedNever();
    }
}
