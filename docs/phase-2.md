# Giai đoạn 2 — SQL Server, báo cáo và concurrency

## Mục tiêu và khái niệm cần hiểu

Chuyển từ “truy vấn trả đúng dữ liệu” sang “truy vấn có hành vi đo được, schema có thể tái tạo và cập nhật an toàn khi có cạnh tranh”. Giai đoạn này bổ sung báo cáo doanh thu qua stored procedure/Dapper, dữ liệu từ 100.000 đơn để đo index, quy trình đọc execution plan và kiểm thử hai người mua sản phẩm cuối cùng.

Cần phân biệt ba cơ chế:

- **Transaction** bảo đảm cập nhật đơn và tồn kho cùng commit/rollback.
- **Isolation level** quy định transaction nhìn thấy dữ liệu và tương tác với transaction khác như thế nào.
- **Optimistic concurrency** dùng rowversion để phát hiện một bản ghi đã thay đổi sau lần đọc, tránh ghi đè hoặc bán quá tồn.

Không thêm endpoint thanh toán trong giai đoạn này. Dữ liệu Paid/Shipped/Completed phục vụ báo cáo được seed trực tiếp cho demo, không đại diện cho một giao dịch thanh toán thực.

## Cấu trúc bổ sung

```text
src/OrderPayment.Application/     Contract và use case báo cáo
src/OrderPayment.Infrastructure/  Migration, index, stored procedure, Dapper
src/OrderPayment.Api/             Controller báo cáo chỉ Admin
scripts/                         Seed dữ liệu lớn và chạy phép đo SQL
docs/sql-tuning.md                Quy trình đo và bằng chứng thực tế
tests/                           Test báo cáo và cạnh tranh trên SQL Server
```

Migration `20261109091047_AddRevenueReporting` tạo `dbo.usp_GetDailyRevenue` và các covering index. Index `IX_Orders_CreatedAt_Revenue` dùng key `CreatedAt`, INCLUDE `CustomerId`, `Status`, `TotalAmount`; index theo khách hàng/ngày dùng key `(CustomerId, CreatedAt)`, INCLUDE `Status`, `TotalAmount`. Hai đường truy cập phục vụ báo cáo toàn hệ thống và báo cáo một khách hàng.

Migration là nguồn quản lý thay đổi schema. Không tạo bảng bằng `EnsureCreated`, không sửa database thủ công rồi coi schema đó đã được quản lý bởi EF. `Database__MigrateOnStartup=true` chỉ dùng để chạy thuận tiện trong môi trường phát triển; production nên áp dụng migration qua bước triển khai có kiểm soát với tài khoản quyền phù hợp.

## Chạy và kiểm tra

Từ thư mục repository, chuẩn bị SQL Server và chạy API:

```bash
bash scripts/init-dev-env.sh
docker compose --env-file .env up -d sqlserver
bash scripts/run-api.sh
```

Chờ service SQL healthy trước lệnh cuối. `run-api.sh` đọc `.env` đã tạo cục bộ, bật migration và chạy API cổng 8080. Không in hoặc đưa `.env` lên Git.

Chạy seed và benchmark bằng wrapper đã cấu hình database lab riêng:

```bash
bash scripts/run-sql-lab.sh 100000
```

Script đặt kết nối đến `OrderPaymentBenchmark`, tự áp dụng migration rồi gọi:

```bash
dotnet run --project tools/OrderPayment.Tools -c Release -- seed --orders 100000 --allow-demo-data
dotnet run --project tools/OrderPayment.Tools -c Release -- benchmark --output artifacts/sql-tuning --allow-benchmark
```

Hai lệnh CLI trực tiếp cần có `ConnectionStrings__DefaultConnection` do người dùng cấp an toàn; wrapper xử lý bước đó từ `.env` local mà không in secret. Seed chỉ nhận database tên kết thúc `Demo`, `Benchmark` hoặc `Tests`, benchmark chỉ nhận `Benchmark`. Từ 100.000 đến 1.000.000 đơn, với khách hàng/sản phẩm/dòng hàng mẫu và ID xác định để chạy lại không tạo trùng. Không biến seed này thành bước startup API.

Benchmark sao chép dữ liệu vào bảng `dbo.SqlTuningOrders` của lab rồi đo trước/sau index trên bảng đó; không drop index của bảng `Orders` thật. Bằng chứng gồm execution plan `.sqlplan`, output raw `STATISTICS IO/TIME` và `summary.json` trong `artifacts/sql-tuning`. Dữ liệu kết quả trước/sau phải khớp nhau; giảm IO mà đổi kết quả không phải tối ưu đúng.

Quy trình seed và đo chi tiết nằm trong [sql-tuning.md](sql-tuning.md). Chỉ chạy seed/benchmark trên database demo riêng vì dữ liệu sinh ra làm thay đổi phân bố thống kê và benchmark có thể tạo/gỡ index phục vụ so sánh. Không chạy trên database chứa dữ liệu cần giữ của người dùng.

Mặc định API CRUD dùng `OrderPayment`, tách biệt với database lab. Để xem dữ liệu đã seed qua API, dừng process API host đang chạy rồi dùng:

```bash
SQL_DATABASE=OrderPaymentBenchmark bash scripts/run-api.sh
```

Sau khi API và dữ liệu đã sẵn sàng, đăng nhập Admin và gọi:

```http
GET /api/reports/revenue/daily?from=2025-01-01&toExclusive=2025-02-01
Authorization: Bearer <accessToken>
```

Quy tắc của báo cáo:

- Ngày được hiểu theo UTC. Khoảng `[from, toExclusive)` bao gồm đầu khoảng, loại trừ cuối khoảng; không dùng mốc cuối ngày kiểu `23:59:59.999` dễ sai độ chính xác.
- Có thể thêm `customerId` để lọc một khách hàng.
- Trả danh sách dòng gồm `date`, `orderCount`, `revenue`, không bổ sung ngày 0. Khoảng tối đa 366 ngày; thiếu/sai ngày, `from >= toExclusive`, khoảng quá dài hoặc customer GUID rỗng nhận `400`. Chỉ tính đơn `Paid`, `Shipped`, `Completed`.
- Nhóm theo ngày tạo đơn `CreatedAt` UTC và trạng thái hiện tại, **không phải ngày thanh toán** vì chưa có `PaidAt`. Kết quả lịch sử có thể thay đổi khi trạng thái đổi.
- Staff nhận `403`; tham số thời gian không hợp lệ nhận `400` ProblemDetails.
- Tạo đơn bằng CRUD không làm phát sinh doanh thu vì đơn mới ở `Pending`.

Request mẫu có trong [order-payment.http](../requests/order-payment.http). Chạy toàn bộ test với Docker hoạt động:

```bash
dotnet test
```

Integration test dùng SQL Server thật và chạy migration. Nhóm test HTTP không truy cập SQL chỉ xác minh pipeline/validation, không chứng minh stored procedure, index hoặc transaction hoạt động. Môi trường không tải được image SQL Server là blocker; không đổi sang EF InMemory hoặc bỏ assertion để tạo kết quả xanh.

## Stored procedure và isolation

`dbo.usp_GetDailyRevenue` nhận `@FromUtc`, `@ToUtcExclusive` kiểu `datetimeoffset(7)` và `@CustomerId` tùy chọn. Nguồn SQL được lưu trong `src/OrderPayment.Infrastructure/Persistence/Sql/CreateDailyRevenueProcedure.sql`, đóng gói cùng migration để schema và logic truy vấn có version.

Procedure lọc trực tiếp `CreatedAt >= @FromUtc AND CreatedAt < @ToUtcExclusive` để có thể dùng index, rồi group ngày UTC bằng `SWITCHOFFSET`. Không dùng `SELECT *` hoặc gọi một truy vấn cho mỗi khách hàng. Dapper nhận parameter, gọi procedure và truyền CancellationToken; không nối trực tiếp dữ liệu input thành câu SQL.

Procedure đặt `READ COMMITTED` để không đọc dữ liệu chưa commit. Chỉ có một SELECT tổng hợp nên không thêm `BEGIN TRAN` giả tạo; không cần atomicity giữa nhiều thao tác báo cáo. SQL Server khôi phục isolation của caller khi procedure kết thúc. Đây không phải cam kết snapshot toàn database: với cấu hình locking READ COMMITTED, các hàng vẫn có thể thay đổi trong quá trình truy vấn. Nếu cần báo cáo nhất quán theo snapshot, phải đánh giá snapshot/RCSI riêng.

`OPTION (RECOMPILE)` cho phép tối ưu theo trường hợp có hoặc không lọc khách hàng, tránh ép mọi tham số dùng một cached plan. Đổi lại mỗi lần gọi có CPU compile; cần đo khi lưu lượng báo cáo tăng.

## Đọc kết quả tuning đúng cách

Trình tự cần thực hiện và lưu bằng chứng trong `docs/sql-tuning.md`:

1. Ghi số lượng dữ liệu, phân bố khách hàng/trạng thái, phiên bản SQL Server và tham số truy vấn.
2. Đo truy vấn gốc với `SET STATISTICS IO, TIME ON`, lưu actual execution plan nếu client hỗ trợ.
3. Thêm index/viết lại predicate theo giả thuyết cụ thể, chạy lại cùng tham số và điều kiện đo.
4. So sánh logical reads, thời gian và toán tử plan. Đọc số liệu từng bảng thay vì chỉ nhìn tổng thời gian một lần chạy.
5. Ghi đánh đổi: chi phí storage, insert/update/delete và bảo trì thống kê/index.

Index tốt cho một truy vấn không mặc nhiên tốt cho toàn hệ thống. Thứ tự cột key cần bám vào equality/range/order của truy vấn; `INCLUDE` phủ các cột đọc mà không đẩy tất cả vào key. Không dùng `SELECT *`. Không dùng `NOLOCK` cho doanh thu vì có thể đọc dữ liệu chưa commit hoặc thiếu/trùng hàng.

Khi chưa thực thi trên SQL Server thật, chỉ mô tả giả thuyết và quy trình; không tự điền “giảm 90%” hay số logical reads. Một estimated plan không phải actual plan, một file `.sql` không phải phép đo đã chạy.

## Kịch bản cạnh tranh

Hai request cùng đặt sản phẩm còn 1 đơn vị có thể cùng vượt qua bước kiểm tra số lượng nếu chỉ đọc rồi so sánh trong bộ nhớ. Mỗi request đọc rowversion của Product; EF đưa token đó vào lệnh UPDATE. Request thắng thay đổi rowversion, request thua gặp concurrency exception và toàn bộ transaction bị rollback. Sau cùng chỉ một đơn giữ được tồn, stock không âm.

Tương tự, sửa/hủy cùng một đơn dùng rowversion của Order và Product. DELETE lặp sau khi đã thấy `Cancelled` trả `204` mà không hoàn tồn thêm; DELETE thật sự chạy đồng thời có thể nhận `409`, client cần đọc lại trạng thái trước khi thử lại. Không tự retry POST tạo đơn vì chưa có idempotency key cho thao tác đó.

Trade-off của optimistic concurrency: không giữ khóa trong suốt thời gian người dùng thao tác, nhưng ứng dụng phải xử lý xung đột. Với contention cao cần đo lại để cân nhắc câu lệnh cập nhật tồn có điều kiện, pessimistic locking hoặc hàng đợi; không chọn chỉ vì tên kỹ thuật nghe nâng cao.

## Câu hỏi phỏng vấn

### 1. Vì sao không chỉ nhìn thời gian chạy để đánh giá index?

Thời gian chịu ảnh hưởng cache, tải máy và IO vật lý. Logical reads phản ánh số trang SQL Server phải đọc, execution plan cho biết đường truy cập và toán tử. Cần đo nhiều lần với cùng tham số, xem cả độ đúng và chi phí ghi; không kết luận từ một lần chạy rất nhanh.

### 2. Khi nào dùng EF Core, khi nào dùng Dapper/stored procedure?

EF Core phù hợp CRUD, tracking và quản lý migration. Báo cáo cần truy vấn SQL rõ ràng có thể dùng Dapper để map kết quả nhẹ và stored procedure để tập trung logic SQL. Đổi lại phải tự quản lý tên cột, kiểu dữ liệu, parameter và version của procedure. Không viết mọi CRUD bằng hai công nghệ cùng lúc.

### 3. Vì sao dùng khoảng ngày có cuối loại trừ?

`CreatedAt >= from AND CreatedAt < toExclusive` diễn đạt đầy đủ khoảng ngày mà không phụ thuộc precision của timestamp. Điều kiện trực tiếp trên cột có thể tận dụng index tốt hơn bọc cột trong `CAST` để lọc. Phép chuyển sang ngày có thể dùng ở projection/grouping sau khi đã lọc.

### 4. Có nên dùng NOLOCK cho báo cáo để đỡ block không?

Không dùng khi cần số doanh thu đáng tin: NOLOCK có thể đọc dirty data hoặc thấy thiếu/trùng hàng. Chọn isolation theo yêu cầu nhất quán và cấu hình database, đo blocking thực tế rồi mới cân nhắc snapshot/read committed snapshot. Báo cáo thống kê không cần tự động lấy khóa ghi trên mọi đơn.

### 5. Vì sao test concurrency cần database thật và request song song?

Unit test không mô phỏng được lệnh UPDATE có điều kiện rowversion và rollback của SQL Server. Test phải cùng tranh một lượng tồn hữu hạn, xác nhận số lần thành công, phản hồi xung đột, số đơn đã lưu và tồn cuối cùng. Chỉ kiểm tra có exception hoặc chỉ gọi tuần tự không chứng minh tình huống cạnh tranh.

## Giới hạn và điểm dừng

Xem [validation.md](validation.md) để biết chính xác kiểm tra nào đã chạy và blocker nào còn tồn tại. Không suy diễn các phép đo Giai đoạn 2 từ kết quả Giai đoạn 1. Sau khi review Giai đoạn 2 mới triển khai fake gateway, webhook, idempotency payment, outbox và background jobs của Giai đoạn 3.
