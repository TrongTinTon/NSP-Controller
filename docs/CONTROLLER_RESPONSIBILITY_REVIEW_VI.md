# Rà soát trách nhiệm Controller NSP

## Kết luận

Source Controller đã được thu gọn đúng vai trò **acquisition and execution**. Controller không còn xử lý nghiệp vụ User, Vehicle, RFID Whitelist, RFID Assignment, Parking Layout, Check-in/Check-out hoặc Parking Transaction.

## Nghiệp vụ Controller thực hiện

1. Xác thực Core API với Edge.
2. Gửi heartbeat bằng `controller_code`.
3. Kéo cấu hình kỹ thuật Reader.
4. Ghép cấu hình runtime với endpoint vật lý được lưu cục bộ.
5. Khởi động và cô lập từng Reader runtime.
6. Áp dụng Power, Read Interval, TID Address, TID Length và danh sách Reader Port.
7. Báo trạng thái Reader và `ports`.
8. Nhận raw RFID detection từ SDK.
9. Lưu durable outbox trước khi gửi Edge.
10. Thực thi Lane Calibration bằng Reader + Port.
11. Gửi raw Calibration Event và trạng thái session.

## Dữ liệu Controller gửi

Parking:

```text
event_uid, serial_number, port_no, detected_at, tid
```

Lane Calibration:

```text
event_uid, measurement_code, revision, power_dbm,
read_interval_ms, serial_number, port_no, tid, read_at, rssi_dbm
```

Controller không gắn User/Vehicle và không xác định event type.

## Các phần đã loại bỏ hoặc tối ưu

- Loại `antenna_code`, `antenna_no`, cây Antenna và mapping Antenna khỏi domain/API/database.
- Loại `controller_code` lặp lại trong từng detection; controller được khai báo ở envelope.
- Loại các field và abstraction không được sử dụng.
- Loại mật khẩu PostgreSQL mặc định khỏi source.
- Sửa vòng đời `HttpResponseMessage`.
- Chuẩn hóa Lane Calibration trong code; chỉ giữ tên route `measurement` do contract Edge.
- Sử dụng giá trị Power/Interval thực tế đã áp dụng khi tạo Calibration Event.
- Xử lý response Calibration theo từng item để không làm mất dữ liệu partial failure.
- Khi Reader runtime dừng, trạng thái offline được lưu cục bộ để lần report tiếp theo không gửi trạng thái online cũ.
