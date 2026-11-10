# Giai đoạn 4 — Kiểm thử và vận hành demo

## Mục tiêu và cấu trúc

Biến mã nguồn thành một portfolio có thể dựng lại, quan sát và review: test đúng hành vi, logs có correlation ID, secrets nằm ngoài Git, Docker chạy các thành phần cùng nhau và CI chạy một lệnh kiểm thử thống nhất.

```text
tests/OrderPayment.UnitTests/         Domain, validation, HMAC, gateway retry
tests/OrderPayment.IntegrationTests/  HTTP pipeline, mock SQLite, SQL Testcontainers
.github/workflows/ci.yml              Restore, Release build, test, lưu TRX
Dockerfile / compose.yaml             API + SQL Server + mock host
scripts/                             Local secrets, startup, SQL lab
requests/                            Kịch bản demo HTTP
docs/validation.md                    Kết quả đo/kiểm thử thực tế và blocker
```

## Kiểm tra

```bash
dotnet restore
dotnet build -c Release --no-restore
dotnet test -c Release --no-build
```

SQL integration tự dựng SQL Server riêng qua Testcontainers và chạy tất cả migration. Test không dùng database ứng dụng đang chạy và không thay SQL Server bằng EF InMemory. Docker phải hoạt động, có đủ bộ nhớ và quyền tải image SQL Server/Ryuk. Fixture lỗi hoặc không tải được image là lỗi prerequisite; không gọi đó là test pass hoặc skip thành công.

Các kiểm tra độc lập với SQL:

```bash
dotnet test tests/OrderPayment.UnitTests -c Release
dotnet test tests/OrderPayment.IntegrationTests -c Release --filter FullyQualifiedName~HttpPipelineTests
dotnet test tests/OrderPayment.IntegrationTests -c Release --filter FullyQualifiedName~SimulatorTests
```

Nhóm unit kiểm tra tiền, trạng thái, callback signature và retry thật của HttpClient bằng handler có kiểm soát. HTTP dùng WebApplicationFactory với JWT/validator/middleware thật; mock host dùng SQLite riêng và kiểm tra qua restart. SQL tests kiểm tra API → SQL, callback lặp/đồng thời, uniqueness, transaction, outbox, reconciliation, checkpoint và stale rowversion. Những lớp kiểm tra này bổ sung cho nhau, không thay thế nhau.

## Logs, health và cấu hình

API ghi Serilog JSON, có `X-Correlation-ID`. ID do client gửi chỉ được giữ nếu có dạng GUID, nếu không server sinh mới. Không log payload webhook, mật khẩu, JWT, connection string, email khách hàng hoặc chữ ký. Một PaymentReference/MessageId giúp tra luồng mà không cần đưa dữ liệu cá nhân vào log.

`GET /health/live` cho biết process sống; `GET /health` kiểm tra database và có thể trả unhealthy khi SQL không kết nối được. Một port mở không chứng minh ứng dụng sẵn sàng. CORS dùng origin được cấu hình, không mở tùy tiện mọi origin; rate limiting cơ bản giảm bớt request dồn và thử mật khẩu.

```bash
bash scripts/init-dev-env.sh
docker compose --env-file .env config --quiet
docker compose --env-file .env up --build -d
docker compose --env-file .env logs -f api
docker compose --env-file .env down
```

Không đưa output chứa cấu hình secret lên issue/log công khai. `.env` được gitignore, mode 600. `down` giữ volume; `down -v` xóa dữ liệu và không phải bước chạy thường ngày. Volume SQL và SQLite mock là độc lập; xóa một bên có thể làm mất khả năng đối soát dữ liệu của bên kia.

Compose là cấu hình phát triển: SQL dùng Developer/SA, chứng chỉ self-signed, Swagger Development và bootstrap hai tài khoản. Production cần user store, secret rotation, quyền SQL tối thiểu, TLS, kế hoạch migration/backup, metrics/alert, giới hạn lưu trữ và quy trình hoàn tiền. Không coi demo này là hệ thống xử lý tiền thật.

## Năm câu hỏi phỏng vấn

### 1. Vì sao test SQL không dùng EF InMemory?

InMemory không mô phỏng rowversion, unique constraint, locking, transaction hay stored procedure của SQL Server. Nó có thể pass trong khi hành vi production sai.

### 2. Test retry nên kiểm tra điều gì?

Đếm số HTTP attempts, chỉ retry lỗi tạm thời, giữ reference/body ổn định và dừng ở giới hạn. Không chỉ kiểm tra có response cuối cùng hoặc dùng handler luôn thành công.

### 3. Liveness và readiness khác nhau thế nào?

Liveness cho biết process còn phục vụ được. Readiness kiểm tra dependency cần cho request; process sống nhưng SQL hỏng thì chưa sẵn sàng xử lý nghiệp vụ.

### 4. Correlation ID có thay thế logging payload không?

ID liên kết log của cùng request và các định danh kỹ thuật giúp tìm sự kiện. Payload nhạy cảm thường không cần để vận hành; cần cấu trúc log và bảo vệ dữ liệu trước khi tăng lượng log.

### 5. CI xanh chứng minh được gì?

Chứng minh các check đã cấu hình hoàn thành ở commit và môi trường đó. Nó không chứng minh deployment, hiệu năng production hoặc những kịch bản chưa test. Badge chưa chạy không phải bằng chứng thành công.

Xem [validation.md](validation.md) cho kết quả hiện tại, bao gồm các phần chưa chạy được. Không bịa execution plan hoặc số liệu logical reads khi SQL chưa hoạt động.
