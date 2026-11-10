using System.Data;
using Microsoft.Data.SqlClient;

namespace OrderPayment.Infrastructure.Jobs;

// LockOwner=Session: connection must remain open until all external calls and DB writes finish.
internal sealed class SqlJobLease(SqlConnection connection, string resource) : IAsyncDisposable
{
    public static async Task<SqlJobLease?> TryAcquireAsync(string connectionString, string name, CancellationToken ct)
    {
        var connection = new SqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(ct);
            var resource = "OrderPayment.Job." + name;
            await using var command = new SqlCommand("""
                DECLARE @result int;
                EXEC @result=sys.sp_getapplock @Resource=@Resource,@LockMode='Exclusive',
                    @LockOwner='Session',@LockTimeout=0;
                SELECT @result;
                """, connection);
            command.Parameters.Add("@Resource", SqlDbType.NVarChar, 255).Value = resource;
            var result = Convert.ToInt32(await command.ExecuteScalarAsync(ct));
            if (result >= 0) return new(connection, resource);
            await connection.DisposeAsync();
            return null;
        }
        catch { await connection.DisposeAsync(); throw; }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await using var command = new SqlCommand("EXEC sys.sp_releaseapplock @Resource=@Resource,@LockOwner='Session';", connection) { CommandTimeout = 5 };
            command.Parameters.Add("@Resource", SqlDbType.NVarChar, 255).Value = resource;
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }
        catch (Exception)
        {
            // A pooled connection must not retain an orphan session lock after release fails.
            SqlConnection.ClearPool(connection);
        }
        finally { await connection.DisposeAsync(); }
    }
}
