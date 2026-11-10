SET NOCOUNT ON;
SET XACT_ABORT ON;
IF (SELECT COUNT_BIG(1) FROM dbo.Orders)<100000
    THROW 51004, 'Seed at least 100000 Orders before benchmarking.', 1;

-- Only rebuild the scratch table created by this tool; never touch application indexes.
IF OBJECT_ID(N'dbo.SqlTuningOrders',N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.extended_properties
                   WHERE major_id=OBJECT_ID(N'dbo.SqlTuningOrders') AND minor_id=0
                     AND name=N'OrderPaymentToolOwned' AND CONVERT(nvarchar(100),value)=N'v1')
    THROW 51005, 'An unrecognized SqlTuningOrders table exists; refusing to replace its data.', 1;

BEGIN TRANSACTION;
IF OBJECT_ID(N'dbo.SqlTuningOrders',N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SqlTuningOrders
    (
        Id uniqueidentifier NOT NULL CONSTRAINT PK_SqlTuningOrders PRIMARY KEY CLUSTERED,
        CustomerId uniqueidentifier NOT NULL,
        CreatedAt datetimeoffset(7) NOT NULL,
        Status nvarchar(20) NOT NULL,
        TotalAmount decimal(18,2) NOT NULL
    );
    EXEC sys.sp_addextendedproperty @name=N'OrderPaymentToolOwned',@value=N'v1',
        @level0type=N'SCHEMA',@level0name=N'dbo',@level1type=N'TABLE',@level1name=N'SqlTuningOrders';
END;

DROP INDEX IF EXISTS IX_SqlTuningOrders_CustomerId_CreatedAt ON dbo.SqlTuningOrders;
TRUNCATE TABLE dbo.SqlTuningOrders;
INSERT dbo.SqlTuningOrders (Id,CustomerId,CreatedAt,Status,TotalAmount)
SELECT Id,CustomerId,SWITCHOFFSET(CreatedAt,'+00:00'),Status,TotalAmount FROM dbo.Orders;
COMMIT;
UPDATE STATISTICS dbo.SqlTuningOrders WITH FULLSCAN;
