# Fake Payment Gateway và Marketplace

Simulator này là một host ASP.NET Core riêng, lưu trạng thái **SQLite** bền vững trong `Fake:DatabasePath` (Compose dùng `/data/fake-external.db` với volume). Không dùng host này làm cổng thanh toán production. Một instance duy nhất sở hữu database; semaphore + SQLite transaction điều phối ghi trong instance đó. Native SQLite provider không cung cấp I/O async thực sự dù interface có Async; đây là giới hạn của simulator, SQL Server vẫn phục vụ ứng dụng chính.

Cấu hình bí mật qua biến môi trường:

| Biến | Ý nghĩa |
| --- | --- |
| `Fake__AdminKey` | Ít nhất 32 ký tự; gửi bằng `X-Simulator-Key` cho `/admin/*` |
| `Fake__DatabasePath` | Đường dẫn SQLite có quyền ghi; mặc định `data/fake-external.db` |
| `Fake__ApiBaseUrl` | API chính nhận callback; mặc định `http://api:8080` |
| `Payments__WebhookSecret` | Ít nhất 32 ký tự; phải khớp secret HMAC của API chính |

`GET /health` kiểm tra SQLite; `GET /health/live` kiểm tra host. Simulator fail startup nếu thiếu hai secret. Không log body, header xác thực hoặc connection string.

## Payment Gateway

- `POST /payments` nhận `{ "paymentReference": "...", "amount": 19.99 }`. Reference có 1–100 ký tự chữ/số/`_`/`-`; tiền dương với tối đa hai số thập phân. Response `{paymentReference,status,amount}`, status ban đầu `Pending`.
- Reference là primary key; tạo lặp với cùng số tiền trả cùng giao dịch, khác số tiền trả `409`. Số tiền lưu dưới dạng cent nguyên, không dùng floating point.
- `GET /payments/{reference}` trả trạng thái `Pending`, `Succeeded` hoặc `Failed` và số tiền; không tồn tại trả `404`.
- `POST /admin/payments/{reference}/settle` nhận `{ "status": "Succeeded" }` hoặc `Failed`. Chỉ chuyển Pending → terminal; cùng terminal là idempotent, chuyển sang terminal khác trả `409`.
- `POST /admin/payments/{reference}/callback?count=3` gửi lại callback terminal từ 1–5 lần, cùng EventId. Chưa settle trả `409`.

Settle lưu trạng thái/EventId trước khi gọi API; gateway không rollback trạng thái nếu callback thất bại. Response chứa `webhookDelivered=false`; job reconciliation của API phải hỏi lại cổng để khôi phục. Settle lặp cùng kết quả và endpoint callback dùng EventId đã lưu để minh họa delivery lặp lại và xử lý idempotent.

Callback là JSON UTF-8 `{eventId,paymentReference,status,amount}` tới `/api/webhooks/payment`. Chữ ký header `X-Payment-Signature` là hex HMAC-SHA256 trên **chính bytes request body**, secret `Payments:WebhookSecret`. Header chữ ký không phải mật khẩu admin. HTTP local/container là cấu hình demo; production cần TLS, key rotation và quy trình quản lý credentials riêng.

Chỉ trong `Development`, `POST /payments` có thể nhận `X-Fake-Mode`:

| Giá trị | Hành vi |
| --- | --- |
| `flaky` | Lần đầu của reference trả 500, lần sau chấp nhận; attempt counter bền vững |
| `always500` | Reference chưa tồn tại luôn trả 500 |
| `delay` | Chờ 5 giây, có CancellationToken |
| `timeout` | Chờ 30 giây để client timeout, có CancellationToken |

Một reference đã tồn tại được trả lại ngay trước fault injection, để retry có thể lấy đúng giao dịch. Simulator không tự trả thanh toán thành công sau thời gian chờ; người demo phải settle bằng endpoint quản trị.

## Marketplace feed

`POST /admin/marketplace/orders` upsert theo externalId:

```json
{
  "externalId": "market-001",
  "customerEmail": "demo@example.com",
  "customerName": "Người mua demo",
  "items": [{ "sku": "DEMO-001", "quantity": 1 }]
}
```

SKU phải tồn tại ở API chính trước khi import. Simulator chấp nhận field `updatedAt` tùy chọn để tương thích request nhưng **bỏ qua giá trị client**: timestamp server là chuẩn, tăng đơn điệu qua các lần ghi. Backdate theo thời gian client có thể đặt update sau checkpoint và làm mất dữ liệu sync, nên không cho client quyết định mốc này. Gửi lại payload giống hệt trả lại phiên bản hiện tại, không tạo change event mới.

`GET /marketplace/orders?since=<ISO-8601>&until=<ISO-8601>&pageSize=100&cursor=<optional>` trả `{items:[{externalId,updatedAt,customerEmail,customerName,items:[{sku,quantity}]}],nextCursor:null|string}`. `since` và `until` inclusive, `pageSize` từ 1–100. Client giữ cùng mốc thời gian trên mọi trang và chỉ tăng checkpoint khi hết `nextCursor`.

Feed là changelog bền vững có revision tăng dần. Cursor chứa snapshot revision, revision đã đọc, hai mốc thời gian và HMAC chống sửa. Update đến khi đang phân trang không thay đổi các trang của snapshot đang đọc; phiên bản mới được nhận trong lần sync sau. Một externalId có thể xuất hiện nhiều lần với updatedAt tăng dần; consumer phải upsert theo externalId/updatedAt, không coi mỗi dòng feed là đơn mới. SQLite giữ changelog để demo restart; chưa có retention/compaction, cần dọn/thiết kế riêng trước dữ liệu dài hạn.
