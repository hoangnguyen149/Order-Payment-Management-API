# SQL tuning — Giai đoạn 2

Mục tiêu là giải thích một phép đo có thể lặp lại: truy vấn gốc, actual execution plan trước, thay đổi index/truy vấn và logical reads/thời gian sau. Không lấy thời gian của một request HTTP làm thời gian SQL, không bịa số liệu và không suy luận toán tử từ tên index.

## Trạng thái phép đo trong cloud

**Chưa có số liệu SQL thực đo trong phiên này.** Docker daemon chạy được nhưng image SQL Server bị HTTP 403 ở domain redirect `centralus.data.mcr.microsoft.com`. Build tool thành công không chứng minh seed hoặc benchmark đã thực thi. Xem [validation.md](validation.md) để biết kết quả kiểm tra của từng giai đoạn.

| Đại lượng | Trước tối ưu | Sau tối ưu |
| --- | --- | --- |
| Số đơn thực sự seed | Chưa chạy SQL | Cùng bộ dữ liệu |
| Logical reads | Chưa đo | Chưa đo |
| SQL CPU / elapsed time | Chưa đo | Chưa đo |
| Actual physical operators | Chưa thu actual plan | Chưa thu actual plan |

Khi SQL Server truy cập được, tool sẽ tạo bằng chứng bên dưới. Chỉ điền bảng kết quả bằng `summary.json`, raw statistics và `.sqlplan` của cùng một lần chạy. Không dùng estimated plan thay cho actual plan.

## Chuẩn bị dữ liệu riêng cho thí nghiệm

Từ thư mục gốc repository, đặt `ConnectionStrings__DefaultConnection` qua cấu hình bí mật cục bộ đến database **`OrderPaymentBenchmark`** trên SQL Server 2022. Không in connection string ra terminal/log và không đưa giá trị mật khẩu vào Git. Ví dụ cấu trúc không chứa giá trị thật: `Server=...;Database=OrderPaymentBenchmark;User Id=...;Password=...;Encrypt=True;...`.

```bash
dotnet run --project tools/OrderPayment.Tools -- seed --orders 100000 --allow-demo-data
dotnet run --project tools/OrderPayment.Tools -- benchmark --output artifacts/sql-tuning --allow-benchmark
```

Có thể dùng wrapper đã đọc `.env` tin cậy cục bộ:

```bash
bash scripts/init-dev-env.sh
docker compose --env-file .env up -d sqlserver
bash scripts/run-sql-lab.sh
```

Tool `seed` tự áp dụng migration, sau đó tạo 1.000 khách hàng, 100 sản phẩm, mặc định 100.000 đơn lịch sử trong năm 2025 và 200.000 dòng hàng. Giá trên hai dòng hàng cộng đúng `TotalAmount`; giá/tên là snapshot. Trạng thái phân bố `Paid`, `Shipped`, `Completed`, `Cancelled`, `PaymentFailed`; không tạo đơn `Pending`, nên dữ liệu lịch sử không phát sinh giữ tồn chưa được giải phóng. Tồn kho demo là số dư đầu kỳ sau lịch sử, không phải một stock ledger đầy đủ.

Seed dùng GUID xác định theo namespace, kiểm tra trùng định danh với dữ liệu không liên quan, insert-only và transaction theo batch 5.000 đơn. Một đơn và hai dòng hàng cùng commit. Chạy lại không thêm trùng hoặc ghi đè dữ liệu; tăng `--orders` bổ sung ID còn thiếu. Nếu ngắt giữa chừng, chạy lại cùng lệnh. Số lượng CLI hợp lệ từ 1 đến 1.000.000; phép đo yêu cầu thực có ít nhất 100.000 đơn.

`--allow-demo-data` là bắt buộc; tên database phải kết thúc bằng `Demo`, `Benchmark` hoặc `Tests`. `benchmark` còn yêu cầu `--allow-benchmark` và hậu tố **`Benchmark`**. Đây là hàng rào tránh nhầm môi trường, không thay thế quyền SQL tối thiểu và việc kiểm tra đúng connection string. Không chạy seed tự động lúc API startup.

Để tránh hai seed chạy cùng lúc, tool giữ session application lock theo tên database trong `master`, từ trước migration đến hết seed. EF8 chưa có migration lock nên phải điều phối như vậy cho các lần chạy tool; API/manual migration không dùng cùng lock thì vẫn cần chạy tuần tự. Account dành cho lab cần quyền tạo/migrate database và quyền kết nối `master` để lấy lock. Lock tự nhả khi connection đóng.

## Truy vấn gốc và giới hạn phép thử

API đã có index từ migration. Không được xóa index đang phục vụ API chỉ để làm kết quả trước tối ưu xấu đi. Tool copy `Orders` sang **`dbo.SqlTuningOrders`** trong database Benchmark riêng và chuẩn hóa `CreatedAt` về UTC. Bảng scratch có clustered primary key `Id`; chưa có secondary index khi chạy baseline.

Tool chỉ xóa/nạp lại bảng scratch có extended property `OrderPaymentToolOwned=v1`; nếu trùng tên với bảng không có marker thì từ chối. Dữ liệu `Orders`, `OrderItems`, sản phẩm, khách hàng và index của API được giữ nguyên. Việc tạo bảng + marker + copy thực hiện trong cùng transaction để retry sau crash không gặp bảng nửa khởi tạo.

Tool chọn một cặp khách hàng/ngày có kết quả không rỗng từ dữ liệu thật đã copy. Cặp tham số được ghi trong `summary.json`; cả hai truy vấn dùng cùng cặp.

```sql
-- Before: chỉ clustered PK Id trên bảng scratch.
SELECT Id, CustomerId, CreatedAt, Status, TotalAmount
FROM dbo.SqlTuningOrders
WHERE CustomerId = @CustomerId
  AND CONVERT(date, CreatedAt) = @Day
ORDER BY CreatedAt, Id
OPTION (RECOMPILE);
```

Ở đây phép chuyển kiểu nằm trên cột lọc. SQL Server có thể biến đổi một số biểu thức thành seek tùy kiểu dữ liệu và index; **không kết luận mọi `CAST`/`CONVERT` đều cấm seek**. Baseline này không có secondary index phù hợp, nên phải xác nhận toán tử và số dòng đọc trong actual plan vừa thu được.

## Index và truy vấn sau

```sql
CREATE INDEX IX_SqlTuningOrders_CustomerId_CreatedAt
ON dbo.SqlTuningOrders(CustomerId, CreatedAt)
INCLUDE(Status, TotalAmount);

SELECT Id, CustomerId, CreatedAt, Status, TotalAmount
FROM dbo.SqlTuningOrders
WHERE CustomerId = @CustomerId
  AND CreatedAt >= @FromUtc
  AND CreatedAt < @ToUtcExclusive
ORDER BY CreatedAt, Id
OPTION (RECOMPILE);
```

`@FromUtc` là 00:00 UTC, `@ToUtcExclusive` là 00:00 UTC ngày kế tiếp. Khoảng nửa mở tránh phụ thuộc độ chính xác phần giây, đúng cả cuối ngày. Key `(CustomerId, CreatedAt)` khớp equality trước, range sau; `Status`/`TotalAmount` là include, và clustered key `Id` được chứa trong nonclustered index. Sau khi chạy, kiểm tra seek predicate, residual predicate, actual rows/read rows, sort và key lookup trong plan thay vì chỉ xem estimated cost phần trăm.

Migration của API triển khai `IX_Orders_CustomerId_CreatedAt` có include tương ứng và thêm `IX_Orders_CreatedAt_Revenue` cho báo cáo ngày trên toàn bộ khách hàng. Index có chi phí lưu trữ và chi phí ghi; không phải cứ thêm include mọi cột là tốt.

Thí nghiệm thay đổi **cả truy vấn lẫn index**. Nó minh họa tổng tác động, không tách riêng mức đóng góp của từng thay đổi, không đại diện đầy đủ tải production và không đánh giá write amplification. Muốn tách nguyên nhân, đo thêm 4 tổ hợp query/index trên cùng snapshot; ngoài phạm vi phép đo này.

## Bằng chứng tool ghi lại

Mỗi lần chạy tạo một thư mục mới `artifacts/sql-tuning/<UTC-time>-<run-id>/`:

| Tệp | Nội dung |
| --- | --- |
| `before.sql`, `after.sql` | Chính xác hai câu SELECT đã chạy |
| `before-1.sqlplan` … `before-3.sqlplan` | Actual execution plan XML trước tối ưu |
| `after-1.sqlplan` … `after-3.sqlplan` | Actual execution plan XML sau tối ưu |
| `before-1-statistics.txt` … `after-3-statistics.txt` | Thông báo SQL Server `STATISTICS IO` / `TIME` nguyên văn |
| `summary.json` | Phiên bản/edition/compatibility SQL, số dòng, tham số mẫu, mỗi lần đo, median, checksum và physical operators lấy từ XML |

Mỗi variant có một warm-up không tính kết quả, rồi ba lần đo cache ấm. Không chạy `DBCC DROPCLEANBUFFERS` hay xóa plan cache chung. Cả hai SELECT dùng `OPTION (RECOMPILE)` để tránh plan cache lần trước chi phối thí nghiệm; CPU/elapsed được thu cho thực thi, parse/compile có trong raw log nếu cần đối chiếu. `ClientElapsedMs` bao gồm network, đọc kết quả và hash phía client, nên không đồng nhất với SQL elapsed. SQL metrics có độ phân giải millisecond, có thể bằng 0 ở truy vấn nhỏ; không làm tròn thành số tự nghĩ.

Tool kiểm tra SHA-256 của các dòng trả về theo thứ tự, số dòng, actual plan XML và đầy đủ capture IO/TIME. Nếu kết quả hai variant khác nhau, không có actual plan hoặc parse statistics không đầy đủ, lệnh thất bại. Capture thiếu giữ raw artifacts và đánh dấu `statisticsComplete=false` nếu đã đến bước ghi summary; không được mô tả lần đo đó là thành công.

Các `.sqlplan` có thể mở trong SSMS hoặc Azure Data Studio có extension phù hợp. Xem [SQL kiểm tra chỉ đọc](sql/inspect-demo.sql) để xác nhận số dòng, phân bố trạng thái, tổng tiền và index trước khi báo cáo số liệu. File artifacts được gitignore; chỉ lưu kết quả được chọn sau khi kiểm tra không có thông tin nhạy cảm.

## Báo cáo doanh thu, transaction và isolation

Stored procedure chuẩn nằm tại [CreateDailyRevenueProcedure.sql](../src/OrderPayment.Infrastructure/Persistence/Sql/CreateDailyRevenueProcedure.sql) và được migration cài đặt. Dapper chỉ gọi procedure với parameter; không nối SQL từ client. Báo cáo chỉ lấy `Paid`/`Shipped`/`Completed`, tổng từ `Orders.TotalAmount` để không nhân doanh thu theo số dòng hàng. Ngày theo UTC và range lọc SARGable; `customerId` là tùy chọn.

Procedure dùng `READ COMMITTED`; một SELECT tổng hợp không cần transaction tường minh riêng. Ở cấu hình mặc định SQL Server, nó có thể chờ writer thay vì đọc bẩn như `NOLOCK`. `READ COMMITTED` không đồng nghĩa một snapshot thời điểm cho toàn bộ các statement; nếu cần báo cáo nhiều SELECT nhất quán, cân nhắc snapshot isolation/RCSI sau khi đánh giá version store và semantics. Isolation không thay thế concurrency token hoặc transaction của thao tác ghi.
