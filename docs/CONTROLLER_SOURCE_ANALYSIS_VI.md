# Phân tích source NSP Controller 1.4.0

## 1. Đánh giá source ban đầu

Source có nền tảng tốt: Reader driver abstraction, PostgreSQL durable outbox, Core API authentication, Zeroconf fallback và worker độc lập. Tuy nhiên mô hình cũ bị lệch so với kiến trúc NSP mới ở các điểm:

- Antenna xuất hiện trong domain, database và API trong khi runtime đã chốt Reader + Port.
- Một số payload còn field cũ hoặc field lặp không được Edge chấp nhận.
- Thuật ngữ Measurement được dùng tràn trong nghiệp vụ Controller dù đây là Lane Calibration.
- Một số abstraction, property và reference không được sử dụng.
- Connection string mẫu chứa thông tin nhạy cảm.
- HTTP response chưa được dispose ở một số luồng.
- Calibration event có nguy cơ ghi Power yêu cầu thay vì Power thực tế áp dụng.
- Partial batch failure có nguy cơ bị đánh dấu thành công toàn bộ.
- Reader đã dừng có thể để lại runtime status online cũ.

## 2. Kiến trúc sau chỉnh sửa

```text
Edge technical config
        ↓
Controller CoreApiClient
        ↓
ReaderManager
  ├── local physical profile
  ├── Reader driver lifecycle
  ├── Parking routing
  └── Lane Calibration routing
        ↓
separate durable outboxes
        ↓
Edge APIs
```

Controller chỉ xử lý dữ liệu kỹ thuật. Edge chịu trách nhiệm resolve TID, áp runtime assignment, đối chiếu Event Sequence và tạo Check-in/Check-out.

## 3. Mô hình dữ liệu chính

- `ReaderDeviceConfig`: serial, endpoint local, Power, Interval, TID range, Ports.
- `RfidDetection`: event UID, Reader serial, Port, TID, timestamp, RSSI transient.
- `LaneCalibrationSessionConfig`: code, status, desired state, revision, Reader + Ports.
- `LaneCalibrationEvent`: raw observation cùng revision và cấu hình thực tế.

Không có User, Vehicle, Whitelist, Assignment, Lane, Event Type hoặc Transaction trong Controller domain.

## 4. Tối ưu độ tin cậy

- Callback SDK chỉ enqueue, không gọi HTTP.
- Hai outbox Parking/Calibration độc lập.
- `event_uid` unique giúp retry idempotent.
- Backoff lưu trong database.
- Batch Calibration xử lý theo từng item.
- Reader lỗi không làm dừng Reader khác.
- Khi Calibration dừng, cấu hình Parking được phục hồi.

## 5. Giới hạn kiểm tra

Source đã được kiểm tra tĩnh nhưng chưa được build/run với SDK .NET Framework và thiết bị CF-E718 trong môi trường này. Trước production cần:

1. Build Release x86 và x64 trên Windows.
2. Test native DLL tương ứng kiến trúc process.
3. Test Reader thật với nhiều Port.
4. Test mất Edge/PostgreSQL và recovery outbox.
5. Test Lane Calibration revision, duplicate, ignored và rejected item.
