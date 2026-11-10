# Xác minh dự án — Giai đoạn 1–4

Kết quả tại môi trường cloud ngày 10/11/2026, SDK .NET 8.0.427, Linux x64. Đây là kết quả của máy hiện tại, không phải chứng nhận CI đã chạy trên GitHub hoặc ứng dụng đã triển khai production.

| Kiểm tra | Kết quả |
| --- | --- |
| Build toàn solution, cấu hình Release | Thành công, 0 warning / 0 error |
| Publish Release API và FakeExternalApi | Thành công cho cả hai project |
| Unit tests | **115 passed, 0 failed, 0 skipped** |
| WebApplicationFactory HTTP/JWT/validation/webhook, không truy cập SQL | **16 passed, 0 failed, 0 skipped** |
| Mock host thật qua WebApplicationFactory + SQLite | **3 passed, 0 failed, 0 skipped**; reference idempotency, HMAC callback, restart, marketplace snapshot/cursor |
| API → SQL Server thật qua Testcontainers | **28 failed tại fixture setup, 0 passed, 0 skipped**; chưa chạy được phần assertion nghiệp vụ SQL |
| Mock host qua Kestrel, kiểm tra thủ công | 33 assertion hoàn thành, gồm restart SQLite và callback HMAC; đây không phải 33 xUnit tests |
| EF model với migration snapshot | Không có thay đổi schema chưa được migration |
| Script migration idempotent | Sinh thành công; chưa áp dụng trên SQL Server trong môi trường này |
| SQL parser ScriptDom 160 | Procedure, seed, benchmark preparation và migration script không có lỗi cú pháp; không thay cho thực thi SQL |
| CLI guardrails | 7 trường hợp kiểm tra đối số/quyền chạy và tên database được xác minh |
| `docker compose --env-file .env config --quiet` | Cấu hình hợp lệ |
| Docker image build / Compose chạy đầy đủ | Chưa xác minh đầy đủ vì không tải được SQL Server image |
| GitHub Actions | Có workflow restore/build/test và lưu TRX; chưa chạy trên GitHub |
| Seed ≥100.000 đơn | Đã có công cụ; chưa thực thi trên SQL Server |
| Execution plan, logical reads và thời gian trước/sau index | Chưa đo; không có số liệu hiệu năng để kết luận |

Lệnh kiểm thử đầy đủ đã thực thi:

```bash
dotnet test OrderPayment.sln -c Release
```

Kết quả tổng: 115 unit test và 19 integration test không cần SQL đã pass; 28 test SQL lỗi ở khởi tạo fixture. Lệnh này **trả exit code thất bại**, không được diễn giải là toàn bộ test xanh. Báo cáo TRX của phiên chạy nằm tại `/tmp/order-payment-tests/complete-final` trên máy cloud hiện tại, không phải artifact CI đã công bố.

## Blocker SQL Server

Docker daemon hoạt động. `docker pull mcr.microsoft.com/mssql/server:2022-latest` thất bại do HTTP 403. Registry trả redirect tới `centralus.data.mcr.microsoft.com`; proxy từ chối CONNECT tới domain đó. Testcontainers báo không tìm thấy image sau khi tải thất bại.

Cần thêm `centralus.data.mcr.microsoft.com` vào allowlist trong cài đặt mạng của môi trường cloud, giữ các domain hiện có. Không tắt TLS, không thay SQL Server bằng EF InMemory để làm test xanh. Công cụ configuration draft của phiên này báo lỗi xác minh, nên chưa có cập nhật allowlist tự động nào được lưu.

Sau khi cấu hình mạng được áp dụng, chạy lại:

```bash
docker pull mcr.microsoft.com/mssql/server:2022-latest
dotnet test -c Release
bash scripts/init-dev-env.sh
docker compose --env-file .env up --build -d
```

Nhóm SQL gồm CRUD/concurrency/báo cáo, payment idempotency, callback lặp/đồng thời, trạng thái đơn sau thanh toán, outbox retry/receipt, reconciliation và marketplace failure replay/checkpoint. Chưa xác nhận các hành vi này trên SQL Server trước khi các assertion thực sự chạy thành công. Có mã test hoặc migration hợp lệ cú pháp không thay cho kết quả thực thi.

Các test HTTP và mock host đã kiểm tra chữ ký HMAC, validation và contract mô phỏng; chúng không chứng minh transaction SQL, application lock, unique constraint hoặc Dapper report đã chạy đúng.

## Kiểm tra độc lập với SQL

```bash
dotnet test tests/OrderPayment.UnitTests -c Release
dotnet test tests/OrderPayment.IntegrationTests -c Release --filter 'FullyQualifiedName~HttpPipelineTests|FullyQualifiedName~SimulatorTests'
```

Filter này chủ động chọn nhóm không truy cập SQL để có phản hồi nhanh khi prerequisite chưa có. Lệnh kiểm thử đầy đủ của dự án vẫn là `dotnet test`; CI không bỏ các test SQL.

SDK của phiên cloud này nằm ngoài checkout. Trước khi chạy CLI trong shell mới của chính máy này:

```bash
export PATH="/workspace/.tools/dotnet:$PATH"
export DOTNET_CLI_HOME=/workspace/.dotnet-home
export NUGET_PACKAGES=/workspace/.nuget/packages
```

Máy cá nhân/CI đã cài .NET SDK 8 không cần ba dòng cấu hình riêng này. `.env` là cấu hình local đã gitignore; không đưa giá trị secret vào báo cáo hoặc mã nguồn. Publish API/mock host, Docker image, CI và benchmark là những kết quả khác nhau: chỉ tuyên bố từng phần khi có bằng chứng tương ứng.
