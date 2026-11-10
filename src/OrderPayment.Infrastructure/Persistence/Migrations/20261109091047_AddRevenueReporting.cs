using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderPayment.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRevenueReporting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Orders_CustomerId_CreatedAt",
                table: "Orders");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_CreatedAt_Revenue",
                table: "Orders",
                column: "CreatedAt")
                .Annotation("SqlServer:Include", new[] { "CustomerId", "Status", "TotalAmount" });

            migrationBuilder.CreateIndex(
                name: "IX_Orders_CustomerId_CreatedAt",
                table: "Orders",
                columns: new[] { "CustomerId", "CreatedAt" })
                .Annotation("SqlServer:Include", new[] { "Status", "TotalAmount" });

            const string resourceName = "OrderPayment.Infrastructure.Persistence.Sql.CreateDailyRevenueProcedure.sql";
            using var stream = typeof(AddRevenueReporting).Assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Missing migration SQL resource: {resourceName}");
            using var reader = new StreamReader(stream);
            // EXEC cho phép CREATE PROCEDURE chạy cả trong script migration --idempotent.
            migrationBuilder.Sql("EXEC(N'" + reader.ReadToEnd().Replace("'", "''") + "');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP PROCEDURE IF EXISTS [dbo].[usp_GetDailyRevenue];");

            migrationBuilder.DropIndex(
                name: "IX_Orders_CreatedAt_Revenue",
                table: "Orders");

            migrationBuilder.DropIndex(
                name: "IX_Orders_CustomerId_CreatedAt",
                table: "Orders");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_CustomerId_CreatedAt",
                table: "Orders",
                columns: new[] { "CustomerId", "CreatedAt" });
        }
    }
}
