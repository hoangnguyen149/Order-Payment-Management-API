CREATE OR ALTER PROCEDURE [dbo].[usp_GetDailyRevenue]
    @FromUtc datetimeoffset(7),
    @ToUtcExclusive datetimeoffset(7),
    @CustomerId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    -- Một SELECT dùng READ COMMITTED; không có dirty read như NOLOCK.
    -- SQL Server phục hồi isolation level của caller khi procedure kết thúc.
    SET TRANSACTION ISOLATION LEVEL READ COMMITTED;

    IF @FromUtc >= @ToUtcExclusive
        THROW 50001, 'FromUtc must precede ToUtcExclusive.', 1;

    SELECT
        CAST(SWITCHOFFSET(o.[CreatedAt], '+00:00') AS date) AS [Day],
        COUNT(*) AS [OrderCount],
        SUM(o.[TotalAmount]) AS [Revenue]
    FROM [dbo].[Orders] AS o
    WHERE o.[CreatedAt] >= @FromUtc
      AND o.[CreatedAt] < @ToUtcExclusive
      AND o.[Status] IN (N'Paid', N'Shipped', N'Completed')
      AND (@CustomerId IS NULL OR o.[CustomerId] = @CustomerId)
    GROUP BY CAST(SWITCHOFFSET(o.[CreatedAt], '+00:00') AS date)
    ORDER BY [Day]
    -- Hai dạng global/customer có độ chọn lọc khác nhau; đánh đổi CPU compile
    -- để tránh một cached plan không phù hợp với tham số của lần gọi tiếp theo.
    OPTION (RECOMPILE);
END;
