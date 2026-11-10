SET NOCOUNT ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

;WITH Digits(n) AS (SELECT n FROM (VALUES (0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) v(n)),
Numbers(n) AS (SELECT 1+a.n+10*b.n+100*c.n FROM Digits a CROSS JOIN Digits b CROSS JOIN Digits c)
SELECT n, CONVERT(uniqueidentifier,CONCAT('d0000000-0000-0000-0000-',RIGHT(CONCAT('000000000000',n),12))) Id
INTO #Customers FROM Numbers;

-- A collision with unrelated pre-existing data must not silently bind demo orders to it.
IF EXISTS (SELECT 1 FROM #Customers s JOIN dbo.Customers c ON c.Id=s.Id
           WHERE c.Email<>CONCAT('seed-',s.n,'@demo.invalid'))
    THROW 51001, 'A demo customer identifier is already occupied by unrelated data.', 1;

INSERT dbo.Customers (Id,FullName,Email,Phone)
SELECT s.Id,CONCAT(N'Demo Customer ',s.n),CONCAT('seed-',s.n,'@demo.invalid'),NULL
FROM #Customers s WHERE NOT EXISTS (SELECT 1 FROM dbo.Customers c WHERE c.Id=s.Id);

;WITH Digits(n) AS (SELECT n FROM (VALUES (0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) v(n))
SELECT 1+a.n+10*b.n n,
       CONVERT(uniqueidentifier,CONCAT('d1000000-0000-0000-0000-',RIGHT(CONCAT('000000000000',1+a.n+10*b.n),12))) Id
INTO #Products FROM Digits a CROSS JOIN Digits b;

IF EXISTS (SELECT 1 FROM #Products s JOIN dbo.Products p ON p.Id=s.Id
           WHERE p.Sku<>CONCAT('DEMO-',RIGHT(CONCAT('000',s.n),3)))
    THROW 51002, 'A demo product identifier is already occupied by unrelated data.', 1;

-- Available stock is an opening balance after historical orders, not a reconstructed stock ledger.
INSERT dbo.Products (Id,Name,Sku,Price,StockQuantity)
SELECT s.Id,CONCAT(N'Demo Product ',s.n),CONCAT('DEMO-',RIGHT(CONCAT('000',s.n),3)),
       CONVERT(decimal(18,2),100+s.n*1.25),50000
FROM #Products s WHERE NOT EXISTS (SELECT 1 FROM dbo.Products p WHERE p.Id=s.Id);
COMMIT;
