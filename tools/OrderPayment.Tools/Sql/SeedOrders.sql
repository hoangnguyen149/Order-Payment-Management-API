SET NOCOUNT ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;
DROP TABLE IF EXISTS #Rows;
DROP TABLE IF EXISTS #Inserted;

;WITH Digits(n) AS (SELECT n FROM (VALUES (0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) v(n)),
Numbers(n) AS (SELECT @Start+a.n+10*b.n+100*c.n+1000*d.n
              FROM Digits a CROSS JOIN Digits b CROSS JOIN Digits c CROSS JOIN Digits d)
SELECT n,
 CONVERT(uniqueidentifier,CONCAT('d2000000-0000-0000-0000-',RIGHT(CONCAT('000000000000',n),12))) Id,
 CONVERT(uniqueidentifier,CONCAT('d0000000-0000-0000-0000-',RIGHT(CONCAT('000000000000',1+(n-1)%1000),12))) CustomerId,
 1+(n-1)%100 Product1,1+n%100 Product2,1+n%3 Quantity1,1+n%2 Quantity2
INTO #Rows FROM Numbers WHERE n<=@End;

IF EXISTS (SELECT 1 FROM #Rows s JOIN dbo.Orders o ON o.Id=s.Id
           WHERE o.CustomerId<>s.CustomerId OR o.CreatedAt<>
               DATEADD(second,(s.n*37)%86400,DATEADD(day,(s.n-1)%365,CONVERT(datetimeoffset(7),'2025-01-01T00:00:00+00:00'))))
    THROW 51006, 'A demo order identifier is already occupied by unrelated data.', 1;

CREATE TABLE #Inserted (Id uniqueidentifier NOT NULL PRIMARY KEY);
INSERT dbo.Orders (Id,CustomerId,CreatedAt,Status,TotalAmount)
OUTPUT inserted.Id INTO #Inserted(Id)
SELECT s.Id,s.CustomerId,
 DATEADD(second,(s.n*37)%86400,DATEADD(day,(s.n-1)%365,CONVERT(datetimeoffset(7),'2025-01-01T00:00:00+00:00'))),
 CASE (s.n+(s.n-1)/1000)%10 WHEN 0 THEN 'Cancelled' WHEN 1 THEN 'PaymentFailed'
             WHEN 2 THEN 'Paid' WHEN 3 THEN 'Shipped' ELSE 'Completed' END,
 CONVERT(decimal(18,2),(100+s.Product1*1.25)*s.Quantity1+(100+s.Product2*1.25)*s.Quantity2)
FROM #Rows s WHERE NOT EXISTS (SELECT 1 FROM dbo.Orders o WHERE o.Id=s.Id);

-- Order + its two lines commit together; retry never adds lines to an already existing order.
INSERT dbo.OrderItems (Id,OrderId,ProductId,ProductName,UnitPrice,Quantity)
SELECT CONVERT(uniqueidentifier,CONCAT('d3000000-0000-0000-0000-',RIGHT(CONCAT('000000000000',s.n*2+v.LineNumber),12))),
 s.Id,
 CONVERT(uniqueidentifier,CONCAT('d1000000-0000-0000-0000-',RIGHT(CONCAT('000000000000',v.ProductNo),12))),
 CONCAT(N'Demo Product ',v.ProductNo),CONVERT(decimal(18,2),100+v.ProductNo*1.25),v.Quantity
FROM #Rows s JOIN #Inserted i ON i.Id=s.Id
CROSS APPLY (VALUES (0,s.Product1,s.Quantity1),(1,s.Product2,s.Quantity2)) v(LineNumber,ProductNo,Quantity);
DROP TABLE #Inserted;
DROP TABLE #Rows;
COMMIT;
