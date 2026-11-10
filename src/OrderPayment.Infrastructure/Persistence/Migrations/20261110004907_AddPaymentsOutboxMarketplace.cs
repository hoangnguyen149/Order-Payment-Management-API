using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderPayment.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentsOutboxMarketplace : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExternalMarketplaceId",
                table: "Orders",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true,
                collation: "Latin1_General_100_BIN2");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "MarketplaceUpdatedAt",
                table: "Orders",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "OutboxDeliveries",
                columns: table => new
                {
                    MessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeliveredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxDeliveries", x => x.MessageId);
                });

            migrationBuilder.CreateTable(
                name: "OutboxMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Payload = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxMessages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Payments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PaymentReference = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    RequiresReview = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastCheckedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Payments", x => x.Id);
                    table.UniqueConstraint("AK_Payments_PaymentReference", x => x.PaymentReference);
                    table.CheckConstraint("CK_Payments_Amount", "[Amount] > 0");
                    table.ForeignKey(
                        name: "FK_Payments_Orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "Orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SyncCheckpoints",
                columns: table => new
                {
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    LastSyncedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncCheckpoints", x => x.Name);
                });

            migrationBuilder.CreateTable(
                name: "SyncFailures",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExternalId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Payload = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Error = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncFailures", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PaymentEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2"),
                    PaymentReference = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentEvents", x => x.Id);
                    table.CheckConstraint("CK_PaymentEvents_Outcome", "[Outcome] IN (N'Processed', N'Ignored')");
                    table.CheckConstraint("CK_PaymentEvents_Status", "[Status] IN (N'Succeeded', N'Failed')");
                    table.ForeignKey(
                        name: "FK_PaymentEvents_Payments_PaymentReference",
                        column: x => x.PaymentReference,
                        principalTable: "Payments",
                        principalColumn: "PaymentReference",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Orders_ExternalMarketplaceId",
                table: "Orders",
                column: "ExternalMarketplaceId",
                unique: true,
                filter: "[ExternalMarketplaceId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_ProcessedAt_NextAttemptAt_CreatedAt",
                table: "OutboxMessages",
                columns: new[] { "ProcessedAt", "NextAttemptAt", "CreatedAt" },
                filter: "[ProcessedAt] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentEvents_EventId",
                table: "PaymentEvents",
                column: "EventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentEvents_PaymentReference",
                table: "PaymentEvents",
                column: "PaymentReference");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_OrderId",
                table: "Payments",
                column: "OrderId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Payments_OrderId_IdempotencyKey",
                table: "Payments",
                columns: new[] { "OrderId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Payments_Status_CreatedAt",
                table: "Payments",
                columns: new[] { "Status", "CreatedAt" })
                .Annotation("SqlServer:Include", new[] { "OrderId", "LastCheckedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SyncFailures_ExternalId",
                table: "SyncFailures",
                column: "ExternalId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SyncFailures_ResolvedAt_NextAttemptAt",
                table: "SyncFailures",
                columns: new[] { "ResolvedAt", "NextAttemptAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OutboxDeliveries");

            migrationBuilder.DropTable(
                name: "OutboxMessages");

            migrationBuilder.DropTable(
                name: "PaymentEvents");

            migrationBuilder.DropTable(
                name: "SyncCheckpoints");

            migrationBuilder.DropTable(
                name: "SyncFailures");

            migrationBuilder.DropTable(
                name: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_Orders_ExternalMarketplaceId",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "ExternalMarketplaceId",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "MarketplaceUpdatedAt",
                table: "Orders");
        }
    }
}
