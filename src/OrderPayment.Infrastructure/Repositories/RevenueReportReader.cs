using System.Data;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using OrderPayment.Application.Abstractions;
using OrderPayment.Application.Contracts;
using OrderPayment.Infrastructure.Persistence;

namespace OrderPayment.Infrastructure.Repositories;

internal sealed class RevenueReportReader(OrderPaymentDbContext db) : IRevenueReportReader
{
    public async Task<IReadOnlyList<RevenueDayResponse>> ReadDailyAsync(
        DateOnly from, DateOnly toExclusive, Guid? customerId, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose) await db.Database.OpenConnectionAsync(ct);
        try
        {
            var command = new CommandDefinition("dbo.usp_GetDailyRevenue", new
            {
                FromUtc = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)),
                ToUtcExclusive = new DateTimeOffset(toExclusive.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)),
                CustomerId = customerId
            }, transaction: db.Database.CurrentTransaction?.GetDbTransaction(),
                commandTimeout: 30, commandType: CommandType.StoredProcedure, cancellationToken: ct);
            var rows = await connection.QueryAsync<RevenueRow>(command);
            return rows.Select(x => new RevenueDayResponse(DateOnly.FromDateTime(x.Day), x.OrderCount, x.Revenue)).ToArray();
        }
        finally
        {
            if (shouldClose) await db.Database.CloseConnectionAsync();
        }
    }

    // SQL DATE được SqlClient đọc dưới dạng DateTime; tránh global Dapper type handler.
    private sealed class RevenueRow
    {
        public DateTime Day { get; set; }
        public int OrderCount { get; set; }
        public decimal Revenue { get; set; }
    }
}
