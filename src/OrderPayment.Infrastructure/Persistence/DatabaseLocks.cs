using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using OrderPayment.Application;

namespace OrderPayment.Infrastructure.Persistence;

internal static class DatabaseLocks
{
    public static async Task AcquireOrderAsync(OrderPaymentDbContext db, Guid orderId, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Order application lock requires an active transaction.");
        var result = new SqlParameter("@result", SqlDbType.Int) { Direction = ParameterDirection.Output };
        var resource = new SqlParameter("@resource", SqlDbType.NVarChar, 255) { Value = $"order:{orderId:N}" };
        await db.Database.ExecuteSqlRawAsync(
            "EXEC @result = sys.sp_getapplock @Resource=@resource, @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=5000;",
            [result, resource], ct);
        if (result.Value is not int code || code < 0)
            throw new ConflictException("Đơn hàng đang được xử lý bởi một yêu cầu khác. Hãy thử lại.");
    }
}
