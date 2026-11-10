-- Read-only inspection after seed. Run against the intended Demo/Benchmark database.
SELECT COUNT_BIG(1) AS OrderCount FROM dbo.Orders;
SELECT COUNT_BIG(1) AS OrderItemCount FROM dbo.OrderItems;
SELECT Status,COUNT_BIG(1) AS OrderCount,SUM(TotalAmount) AS Amount
FROM dbo.Orders GROUP BY Status ORDER BY Status;

-- Must return zero inconsistent demo orders; no SELECT * and no double-counted totals.
SELECT o.Id,o.TotalAmount,SUM(i.UnitPrice*i.Quantity) AS LineSum
FROM dbo.Orders o JOIN dbo.OrderItems i ON i.OrderId=o.Id
WHERE LOWER(CONVERT(varchar(36),o.Id)) LIKE 'd2000000-0000-0000-0000-%'
GROUP BY o.Id,o.TotalAmount HAVING o.TotalAmount<>SUM(i.UnitPrice*i.Quantity);

SELECT i.name,i.is_unique,c.name AS ColumnName,ic.key_ordinal,ic.is_included_column
FROM sys.indexes i JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id
JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
WHERE i.object_id=OBJECT_ID(N'dbo.Orders')
ORDER BY i.name,ic.key_ordinal,c.name;

EXEC dbo.usp_GetDailyRevenue @FromUtc='2025-01-01T00:00:00+00:00',
    @ToUtcExclusive='2025-02-01T00:00:00+00:00',@CustomerId=NULL;
