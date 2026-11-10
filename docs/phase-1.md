# Giai đoạn 1 — Nền tảng API

## Mục tiêu và khái niệm cần hiểu

Sau giai đoạn này, bạn có thể chạy API, đăng nhập lấy JWT, tạo sản phẩm/khách hàng và tạo đơn với giá do server tính. Hiểu đường đi của một request qua controller, service, Domain và EF Core; hiểu DTO khác entity như thế nào; phân biệt validation với bất biến nghiệp vụ và ràng buộc database.

Các thư mục chính:

```text
src/
  OrderPayment.Api/             # REST, cấu hình, authentication, middleware
  OrderPayment.Application/     # Use case, interface, DTO, validator
  OrderPayment.Domain/          # Entity, trạng thái, nghiệp vụ
  OrderPayment.Infrastructure/  # SQL Server, EF Core, persistence
tests/
  OrderPayment.UnitTests/
  OrderPayment.IntegrationTests/
docs/
requests/
```

## Cách tự kiểm tra

1. Làm theo README để chạy SQL Server và API, mở Swagger.
2. Gọi endpoint đăng nhập rồi dùng token Admin để tạo một sản phẩm tồn kho 2, giá 125000 và một khách hàng.
3. Tạo đơn mua 2 sản phẩm đó. Tổng tiền phải bằng 250000, dù client không gửi giá.
4. Thử tạo một đơn khác mua 1 sản phẩm: phải bị từ chối vì hết tồn kho.
5. Hủy đơn đầu; kiểm tra lượng tồn khả dụng đã được trả lại. Không được trả tồn thêm lần nữa khi hủy lặp.
6. Gửi tên rỗng, giá âm, số lượng bằng 0 và trang có kích thước quá giới hạn; kiểm tra `400` cùng nội dung ProblemDetails.
7. Dùng token Staff gọi thao tác quản trị danh mục; kiểm tra `403`. Gọi tài nguyên cần xác thực mà không có token; kiểm tra `401`.
8. Chạy unit và integration test theo README. Integration test cần Docker; không coi một lần chạy chỉ unit test là đã xác minh SQL Server.

File [requests/order-payment.http](../requests/order-payment.http) chứa request mẫu để dùng trong REST Client. Các ID trả về từ server cần được dùng trong request tiếp theo.

## Câu hỏi phỏng vấn và gợi ý trả lời

### 1. Vì sao không nhận tổng tiền từ client?

Client có thể sửa request. Server phải đọc giá của sản phẩm, kiểm tra số lượng và tự tính tổng. Lưu đơn giá trên dòng hàng để lịch sử đơn không thay đổi khi giá danh mục đổi.

### 2. Vì sao dùng DTO thay vì trả entity EF Core?

DTO giới hạn trường công khai, tránh overposting, vòng tham chiếu navigation và việc vô tình phụ thuộc hợp đồng HTTP vào schema lưu trữ. Đổi lại phải viết mapping; ở dự án nhỏ mapping thủ công dễ đọc và debug.

### 3. Transaction và rowversion giải quyết hai vấn đề gì khác nhau?

Transaction bảo đảm đơn và tồn kho cùng thành công hoặc cùng rollback. Rowversion phát hiện dữ liệu đã bị request khác thay đổi kể từ lúc đọc. Cần cả hai để tránh một đơn được tạo trong khi trừ tồn kho thất bại, hoặc hai người mua cùng một sản phẩm cuối cùng.

### 4. JWT có mã hóa dữ liệu người dùng không?

JWT ký phổ biến bảo đảm tính toàn vẹn, không che nội dung payload. Không đưa mật khẩu hoặc dữ liệu nhạy cảm vào claims. Server phải kiểm tra chữ ký, issuer, audience và hạn dùng; role quyết định quyền truy cập endpoint sau khi xác thực.

### 5. Tại sao kiểm thử tích hợp cần SQL Server thật?

EF InMemory không mô phỏng đầy đủ khóa ngoại, unique index, transaction và rowversion của SQL Server. Testcontainers cung cấp database dùng cho kiểm thử, chạy migration và dọn container sau test. Unit test vẫn có ích cho quy tắc thuần và cho phản hồi nhanh.

## Bước tiếp theo sau khi xác nhận

- Giai đoạn 2: seed từ 100.000 đơn, đo logical reads/execution plan thật, index, báo cáo stored procedure/Dapper và minh họa cạnh tranh.
- Giai đoạn 3: fake gateway, HMAC webhook, idempotency có unique constraint, retry có giới hạn, reconciliation, outbox và đồng bộ marketplace có checkpoint/lock.
- Giai đoạn 4: mở rộng unit/integration test, structured logging + correlation ID, rate limiting/CORS/health checks, hoàn thiện Docker/CI và tài liệu vận hành.

Các tiện ích chạy và kiểm thử có thể được chuẩn bị từ sớm; điều đó không đồng nghĩa nghiệp vụ của các giai đoạn sau đã được triển khai.
