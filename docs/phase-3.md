# Giai đoạn 3 — Thanh toán, outbox và đồng bộ

## Mục tiêu và cấu trúc

Hiểu vì sao timeout không đồng nghĩa thất bại, unique constraint khác kiểm tra trùng trong code, và transaction SQL không bao trùm một HTTP request. Tạo một payment bền vững trước khi gọi gateway, nhận callback HMAC, rồi phát ý định qua outbox. Các job đối soát và đồng bộ phải tiếp tục được sau restart.

```text
Application/Payments/             DTO, validator, HMAC, gateway/service ports
Application/Marketplace/          Contract feed theo cửa sổ và cursor
Domain/Payments.cs               Payment, trạng thái cuối và PaymentEvent
Domain/OutboxMessage.cs           Outbox + consumer receipt
Domain/MarketplaceSync.cs         Checkpoint và failure replay
Infrastructure/Payments/          Transaction, gateway HTTP/resilience
Infrastructure/Jobs/              Outbox, reconciliation, marketplace sync
Api/Controllers/                 Payment/webhook/order status
FakeExternalApi/                  Gateway + marketplace riêng, SQLite persistence
```

Migration thứ ba thêm Payment, PaymentEvent, OutboxMessages/Deliveries, SyncCheckpoints/Failures và trường nhận diện đơn ngoài. Cấu hình `Payments__WebhookSecret` là bắt buộc. SQLite chỉ lưu trạng thái của mock host; database nghiệp vụ và integration test nghiệp vụ vẫn là SQL Server 2022.

## Chạy và thử

```bash
bash scripts/init-dev-env.sh
docker compose --env-file .env up --build -d
docker compose --env-file .env logs -f api fake-external
```

Script bổ sung các khóa local mới mà không ghi đè giá trị cũ. API cổng 8080; mock cổng 8090. Mock admin endpoint yêu cầu `X-Simulator-Key` lấy từ cấu hình local; đây là cổng quản trị mô phỏng, không phải thao tác của client thực.

Dùng [file HTTP](../requests/order-payment.http) theo thứ tự:

1. Đăng nhập, tạo sản phẩm/khách hàng và một đơn Pending mới.
2. `POST /api/orders/{id}/payments` với một `Idempotency-Key` hợp lệ; giữ key khi retry. Kết quả Pending là `202`; gateway tạm lỗi được thể hiện qua `GatewayUnavailable`, payment vẫn còn để đối soát.
3. Gửi `POST /admin/payments/{reference}/settle` đến mock với `{"status":"Succeeded"}`. Mock lưu trạng thái rồi gửi callback đã ký.
4. Gửi callback lại với `POST /admin/payments/{reference}/callback?count=3`; đọc lại payment/đơn. Không có thêm tác động Paid hoặc sự kiện PaymentSucceeded cho callback trùng.
5. Gọi `PATCH /api/orders/{id}/status` lần lượt Shipped rồi Completed. Không có quyền tự đặt Paid bằng endpoint này.
6. Tạo một đơn marketplace qua endpoint admin của mock với SKU có thật. Job đọc feed, giữ tồn, upsert theo ExternalId và lưu checkpoint. SKU không tồn tại được lưu vào SyncFailures; sau khi sửa danh mục, job thử lại khi đến hạn.

Thử Failed trên một đơn riêng: đơn thành PaymentFailed và lượng tồn giữ trước đó được trả lại trong transaction. Demo chỉ có một payment cho mỗi đơn; không hỗ trợ tạo giao dịch mới cho đơn Failed. Khi payment đã được khởi tạo, sửa nội dung đơn bằng PUT bị chặn để số tiền callback không lệch với số tiền đã gửi gateway.

`Gateway__SimulationMode` trong Development nhận `flaky`, `delay`, `timeout`, `always500` để kiểm tra resilience. Không dùng chế độ này khi demo luồng thành công. Chính sách HTTP có hai lần retry, exponential delay/jitter, attempt timeout 2 giây và tổng deadline 10 giây. Retry POST chỉ an toàn vì reference ổn định và gateway dedupe cùng reference.

`Jobs__Enabled=false` chỉ dừng hosted loop, không xóa dữ liệu hay vô hiệu hóa API. Các processor vẫn có thể được gọi trực tiếp bởi integration test. Mặc định poll outbox/reconciliation 30 giây, giao dịch Pending quá 2 phút được đối soát, marketplace sync mỗi 120 giây. Có SQL application lock theo job và theo đơn khi cập nhật nghiệp vụ liên quan.

Đọc [payment-flow.md](payment-flow.md) và [idempotency-and-outbox.md](idempotency-and-outbox.md) để xem sequence, scope key và giới hạn at-least-once. Mock marketplace là feed revision; một ExternalId có thể xuất hiện nhiều lần, importer chỉ áp dụng phiên bản mới hơn. Checkpoint có overlap 1 giây và upsert chống trùng.

## Năm câu hỏi phỏng vấn

### 1. Vì sao không chỉ kiểm tra EventId bằng SELECT trước INSERT?

Hai request có thể cùng đọc “chưa tồn tại”. Unique constraint phân xử tại database; transaction và xử lý lỗi trùng giúp lần sau trả lại kết quả cũ mà không áp dụng lại thay đổi.

### 2. Vì sao không giữ transaction trong lúc gọi gateway?

HTTP có thể chậm hoặc mất kết nối; giữ SQL lock xuyên qua network làm giảm throughput và tăng nguy cơ deadlock. Lưu intent trước, giữ reference ổn định rồi dùng callback/đối soát để kết thúc.

### 3. HMAC khác JWT ở webhook như thế nào?

JWT dùng cho client/API. HMAC webhook ký raw payload bằng khóa chung để xác nhận tính toàn vẹn và nguồn biết khóa. Không deserialize rồi serialize trước khi kiểm tra vì byte có thể đổi. Signature hợp lệ vẫn cần kiểm tra amount, EventId và trạng thái.

### 4. Outbox có exactly-once không?

Không. Worker có thể đã gửi nhưng chưa đánh dấu thành công khi process dừng. Phát lại là hợp lệ; consumer cần idempotency riêng theo MessageId. DB transaction chỉ bảo đảm ý định phát không bị tách khỏi nghiệp vụ.

### 5. Checkpoint giúp gì khi job restart?

Checkpoint trong database lưu tiến độ bền vững; overlap và upsert chịu được việc đọc lại. Lỗi từng bản ghi được lưu trước khi tiến checkpoint, có retry độc lập. Distributed lock hạn chế các worker tranh checkpoint nhưng không thay unique constraint.

Kết quả thực thi thực tế nằm trong [validation.md](validation.md). Chưa chạy SQL thì chưa kết luận transaction, lock hay job đã hoạt động đúng trên SQL Server.
