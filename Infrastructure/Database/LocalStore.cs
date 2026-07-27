using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Npgsql;
using NpgsqlTypes;
using NSPGatekeeper.Controller.Domain;
using NSPGatekeeper.Controller.Infrastructure.Logging;

namespace NSPGatekeeper.Controller.Infrastructure.Database
{
    public sealed class LocalStore
    {
        private readonly string _connectionString;
        private readonly FileLogger _logger;

        public LocalStore(string connectionString, FileLogger logger)
        {
            if (string.IsNullOrWhiteSpace(connectionString)) throw new ArgumentException("PostgreSqlConnectionString is required.");
            _connectionString = connectionString;
            _logger = logger;
        }

        public void EnsureSchema()
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "database", "init_database.sql");
            if (!File.Exists(path)) throw new FileNotFoundException("Database schema file not found.", path);
            ExecuteNonQuery(File.ReadAllText(path));
        }

        public void UpsertDeviceConfig(ReaderDeviceConfig config)
        {
            if (config == null || string.IsNullOrWhiteSpace(config.SerialNumber)) return;
            const string sql = @"
INSERT INTO controller_reader
(serial_number, driver_key, device_name, model, endpoint, port, enabled, config_revision, config_hash,
 power_dbm, read_interval_ms, tid_start_address, tid_length, antennas_json, options_json, updated_at)
VALUES
(@serial_number, @driver_key, @device_name, @model, @endpoint, @port, @enabled, @config_revision, @config_hash,
 @power_dbm, @read_interval_ms, @tid_start_address, @tid_length, CAST(@antennas_json AS jsonb), CAST(@options_json AS jsonb), NOW())
ON CONFLICT (serial_number) DO UPDATE SET
 driver_key=EXCLUDED.driver_key,
 device_name=EXCLUDED.device_name,
 model=EXCLUDED.model,
 endpoint=EXCLUDED.endpoint,
 port=EXCLUDED.port,
 enabled=EXCLUDED.enabled,
 config_revision=EXCLUDED.config_revision,
 config_hash=EXCLUDED.config_hash,
 power_dbm=EXCLUDED.power_dbm,
 read_interval_ms=EXCLUDED.read_interval_ms,
 tid_start_address=EXCLUDED.tid_start_address,
 tid_length=EXCLUDED.tid_length,
 antennas_json=EXCLUDED.antennas_json,
 options_json=EXCLUDED.options_json,
 updated_at=NOW();";

            using (var conn = Open())
            using (var cmd = new NpgsqlCommand(sql, conn))
            {
                AddText(cmd, "serial_number", config.SerialNumber.Trim().ToUpperInvariant());
                AddText(cmd, "driver_key", config.DriverKey ?? string.Empty);
                AddText(cmd, "device_name", config.DeviceName);
                AddText(cmd, "model", config.Model);
                AddText(cmd, "endpoint", config.Endpoint);
                cmd.Parameters.AddWithValue("port", config.Port);
                cmd.Parameters.AddWithValue("enabled", config.Enabled);
                cmd.Parameters.AddWithValue("config_revision", config.ConfigRevision);
                AddText(cmd, "config_hash", config.ConfigHash);
                cmd.Parameters.AddWithValue("power_dbm", config.PowerDbm);
                cmd.Parameters.AddWithValue("read_interval_ms", config.ReadIntervalMs);
                cmd.Parameters.AddWithValue("tid_start_address", config.TidStartAddress);
                cmd.Parameters.AddWithValue("tid_length", config.TidLength);
                cmd.Parameters.AddWithValue("antennas_json", JsonConvert.SerializeObject(config.Antennas ?? new List<ReaderAntennaConfig>()));
                cmd.Parameters.AddWithValue("options_json", JsonConvert.SerializeObject(config.Options ?? new Dictionary<string, string>()));
                cmd.ExecuteNonQuery();
            }
        }

        public void DisableDevicesNotIn(IList<string> serialNumbers)
        {
            var serials = (serialNumbers ?? new List<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim().ToUpperInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            const string sql = @"UPDATE controller_reader SET enabled=FALSE, updated_at=NOW() WHERE NOT (serial_number = ANY(@serials))";
            using (var conn = Open())
            using (var cmd = new NpgsqlCommand(sql, conn))
            {
                cmd.Parameters.Add("serials", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = serials;
                cmd.ExecuteNonQuery();
            }
        }

        public IList<ReaderDeviceConfig> GetDeviceConfigs()
        {
            const string sql = @"
SELECT serial_number, driver_key, device_name, model, endpoint, port, enabled, config_revision, config_hash,
       power_dbm, read_interval_ms, tid_start_address, tid_length, antennas_json::text, options_json::text
  FROM controller_reader
 ORDER BY serial_number";
            var result = new List<ReaderDeviceConfig>();
            using (var conn = Open())
            using (var cmd = new NpgsqlCommand(sql, conn))
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    var serial = reader.GetString(0);
                    result.Add(new ReaderDeviceConfig
                    {
                        DeviceCode = serial,
                        SerialNumber = serial,
                        DriverKey = GetNullableString(reader, 1),
                        DeviceName = GetNullableString(reader, 2),
                        Model = GetNullableString(reader, 3),
                        Endpoint = GetNullableString(reader, 4),
                        Port = reader.GetInt32(5),
                        Enabled = reader.GetBoolean(6),
                        ConfigRevision = reader.GetInt32(7),
                        ConfigHash = GetNullableString(reader, 8),
                        PowerDbm = reader.GetInt32(9),
                        ReadIntervalMs = reader.GetInt32(10),
                        TidStartAddress = reader.GetInt32(11),
                        TidLength = reader.GetInt32(12),
                        Antennas = JsonConvert.DeserializeObject<List<ReaderAntennaConfig>>(reader.GetString(13)) ?? new List<ReaderAntennaConfig>(),
                        Options = ToCaseInsensitiveOptions(reader.GetString(14))
                    });
                }
            }
            return result;
        }

        public void UpdateLocalReaderConnection(string serialNumber, string driverKey, string endpoint, int port)
        {
            serialNumber = (serialNumber ?? string.Empty).Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(serialNumber)) throw new ArgumentException("Reader Serial is required.");
            const string sql = @"
UPDATE controller_reader
   SET driver_key=@driver_key,
       endpoint=@endpoint,
       port=@port,
       updated_at=NOW()
 WHERE serial_number=@serial_number";
            using (var conn = Open())
            using (var cmd = new NpgsqlCommand(sql, conn))
            {
                AddText(cmd, "driver_key", string.IsNullOrWhiteSpace(driverKey) ? "cf-e718" : driverKey);
                AddText(cmd, "endpoint", endpoint);
                cmd.Parameters.AddWithValue("port", Math.Max(0, port));
                AddText(cmd, "serial_number", serialNumber);
                if (cmd.ExecuteNonQuery() == 0)
                    throw new InvalidOperationException("Reader configuration was not found: " + serialNumber);
            }
        }

        public void UpsertReaderStatus(ReaderStatus status)
        {
            if (status == null || string.IsNullOrWhiteSpace(status.SerialNumber)) return;
            const string sql = @"
INSERT INTO controller_reader_runtime_status
(serial_number, driver_key, model, endpoint, online, message, firmware_version, antennas_json, config_revision, updated_at)
VALUES
(@serial_number, @driver_key, @model, @endpoint, @online, @message, @firmware_version, CAST(@antennas_json AS jsonb), @config_revision, @updated_at)
ON CONFLICT (serial_number) DO UPDATE SET
 driver_key=EXCLUDED.driver_key,
 model=EXCLUDED.model,
 endpoint=EXCLUDED.endpoint,
 online=EXCLUDED.online,
 message=EXCLUDED.message,
 firmware_version=EXCLUDED.firmware_version,
 antennas_json=EXCLUDED.antennas_json,
 config_revision=EXCLUDED.config_revision,
 updated_at=EXCLUDED.updated_at;";
            using (var conn = Open())
            using (var cmd = new NpgsqlCommand(sql, conn))
            {
                AddText(cmd, "serial_number", status.SerialNumber.Trim().ToUpperInvariant());
                AddText(cmd, "driver_key", status.DriverKey);
                AddText(cmd, "model", status.Model);
                AddText(cmd, "endpoint", status.Endpoint);
                cmd.Parameters.AddWithValue("online", status.Online);
                AddText(cmd, "message", status.Message);
                AddText(cmd, "firmware_version", status.FirmwareVersion);
                cmd.Parameters.AddWithValue("antennas_json", JsonConvert.SerializeObject(status.Antennas ?? new List<int>()));
                cmd.Parameters.AddWithValue("config_revision", status.ConfigRevision);
                cmd.Parameters.AddWithValue("updated_at", NormalizeUtc(status.UpdatedAtUtc));
                cmd.ExecuteNonQuery();
            }
        }

        public IList<ReaderStatus> GetReaderStatuses()
        {
            const string sql = @"
SELECT s.serial_number, s.driver_key, s.model, s.endpoint, s.online, s.message, s.firmware_version,
       s.antennas_json::text, s.updated_at, s.config_revision
  FROM controller_reader_runtime_status s
  JOIN controller_reader r ON r.serial_number = s.serial_number
 WHERE r.enabled = TRUE
 ORDER BY s.serial_number";
            var result = new List<ReaderStatus>();
            using (var conn = Open())
            using (var cmd = new NpgsqlCommand(sql, conn))
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    var serial = reader.GetString(0);
                    result.Add(new ReaderStatus
                    {
                        DeviceCode = serial,
                        SerialNumber = serial,
                        DriverKey = GetNullableString(reader, 1),
                        Model = GetNullableString(reader, 2),
                        Endpoint = GetNullableString(reader, 3),
                        Online = reader.GetBoolean(4),
                        Message = GetNullableString(reader, 5),
                        FirmwareVersion = GetNullableString(reader, 6),
                        Antennas = JsonConvert.DeserializeObject<List<int>>(reader.GetString(7)) ?? new List<int>(),
                        UpdatedAtUtc = reader.GetDateTime(8).ToUniversalTime(),
                        ConfigRevision = reader.GetInt32(9)
                    });
                }
            }
            return result;
        }

        public void EnqueueDetections(IList<RfidDetection> detections)
        {
            if (detections == null || detections.Count == 0) return;
            const string sql = @"
INSERT INTO controller_parking_outbox
(event_uid, controller_code, serial_number, antenna_no, tid, detected_at)
VALUES
(@event_uid, @controller_code, @serial_number, @antenna_no, @tid, @detected_at)
ON CONFLICT (event_uid) DO NOTHING;";
            using (var conn = Open())
            using (var tx = conn.BeginTransaction())
            using (var cmd = new NpgsqlCommand(sql, conn, tx))
            {
                foreach (var detection in detections)
                {
                    if (detection == null || string.IsNullOrWhiteSpace(detection.EventUid) || string.IsNullOrWhiteSpace(detection.Tid)) continue;
                    cmd.Parameters.Clear();
                    AddText(cmd, "event_uid", detection.EventUid);
                    AddText(cmd, "controller_code", detection.ControllerCode);
                    AddText(cmd, "serial_number", detection.DeviceSerial);
                    cmd.Parameters.AddWithValue("antenna_no", detection.AntennaId);
                    AddText(cmd, "tid", detection.Tid.Trim().ToUpperInvariant());
                    cmd.Parameters.AddWithValue("detected_at", NormalizeUtc(detection.DetectedAtUtc));
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }
        }

        public IList<OutboxItem> GetPendingDetections(int limit)
        {
            const string sql = @"
SELECT id, event_uid, controller_code, serial_number, antenna_no, tid, detected_at, attempts
  FROM controller_parking_outbox
 WHERE status='pending' AND next_attempt_at <= NOW()
 ORDER BY id
 LIMIT @limit";
            var result = new List<OutboxItem>();
            using (var conn = Open())
            using (var cmd = new NpgsqlCommand(sql, conn))
            {
                cmd.Parameters.AddWithValue("limit", Math.Max(1, limit));
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        result.Add(new OutboxItem
                        {
                            Id = reader.GetInt64(0),
                            Detection = new RfidDetection
                            {
                                EventUid = reader.GetString(1),
                                ControllerCode = reader.GetString(2),
                                DeviceSerial = reader.GetString(3),
                                DeviceCode = reader.GetString(3),
                                AntennaId = reader.GetInt32(4),
                                Tid = reader.GetString(5),
                                DetectedAtUtc = reader.GetDateTime(6).ToUniversalTime()
                            },
                            Attempts = reader.GetInt32(7)
                        });
                    }
                }
            }
            return result;
        }

        public void MarkSent(IList<long> ids)
        {
            UpdateByIds(ids, "UPDATE controller_parking_outbox SET status='sent', sent_at=NOW(), last_error=NULL WHERE id = ANY(@ids)", null);
        }

        public void MarkDead(IList<long> ids, string error)
        {
            UpdateByIds(ids, "UPDATE controller_parking_outbox SET status='dead', last_error=@error WHERE id = ANY(@ids)",
                delegate(NpgsqlCommand cmd) { AddText(cmd, "error", error ?? "permanent_error"); });
        }

        public void MarkFailed(IList<long> ids, string error, int attempts)
        {
            var delaySeconds = RetryDelaySeconds(attempts);
            UpdateByIds(ids,
                "UPDATE controller_parking_outbox SET attempts=attempts+1, last_error=@error, next_attempt_at=NOW()+make_interval(secs => @delay_seconds) WHERE id = ANY(@ids)",
                delegate(NpgsqlCommand cmd)
                {
                    AddText(cmd, "error", error ?? "push_failed");
                    cmd.Parameters.AddWithValue("delay_seconds", delaySeconds);
                });
        }

        public void EnqueueMeasurementEvents(IList<MeasurementEvent> events)
        {
            if (events == null || events.Count == 0) return;
            const string sql = @"
INSERT INTO controller_measurement_outbox
(event_uid, measurement_code, serial_number, antenna_no, tid, rssi_dbm, read_at)
VALUES
(@event_uid, @measurement_code, @serial_number, @antenna_no, @tid, @rssi_dbm, @read_at)
ON CONFLICT (event_uid) DO NOTHING;";
            using (var conn = Open())
            using (var tx = conn.BeginTransaction())
            using (var cmd = new NpgsqlCommand(sql, conn, tx))
            {
                foreach (var evt in events)
                {
                    if (evt == null || string.IsNullOrWhiteSpace(evt.EventUid) || string.IsNullOrWhiteSpace(evt.MeasurementCode)) continue;
                    cmd.Parameters.Clear();
                    AddText(cmd, "event_uid", evt.EventUid);
                    AddText(cmd, "measurement_code", evt.MeasurementCode);
                    AddText(cmd, "serial_number", evt.SerialNumber);
                    cmd.Parameters.AddWithValue("antenna_no", evt.AntennaNo);
                    AddText(cmd, "tid", evt.Tid);
                    var rssi = cmd.Parameters.Add("rssi_dbm", NpgsqlDbType.Double);
                    rssi.Value = evt.RssiDbm.HasValue ? (object)evt.RssiDbm.Value : DBNull.Value;
                    cmd.Parameters.AddWithValue("read_at", NormalizeUtc(evt.ReadAtUtc));
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }
        }

        public IList<MeasurementOutboxItem> GetPendingMeasurementEvents(int limit)
        {
            const string sql = @"
SELECT id, event_uid, measurement_code, serial_number, antenna_no, tid, rssi_dbm, read_at, attempts
  FROM controller_measurement_outbox
 WHERE status='pending' AND next_attempt_at <= NOW()
 ORDER BY id
 LIMIT @limit";
            var result = new List<MeasurementOutboxItem>();
            using (var conn = Open())
            using (var cmd = new NpgsqlCommand(sql, conn))
            {
                cmd.Parameters.AddWithValue("limit", Math.Max(1, Math.Min(100, limit)));
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        result.Add(new MeasurementOutboxItem
                        {
                            Id = reader.GetInt64(0),
                            Event = new MeasurementEvent
                            {
                                EventUid = reader.GetString(1),
                                MeasurementCode = reader.GetString(2),
                                SerialNumber = reader.GetString(3),
                                AntennaNo = reader.GetInt32(4),
                                Tid = reader.GetString(5),
                                RssiDbm = reader.IsDBNull(6) ? (double?)null : reader.GetDouble(6),
                                ReadAtUtc = reader.GetDateTime(7).ToUniversalTime()
                            },
                            Attempts = reader.GetInt32(8)
                        });
                    }
                }
            }
            return result;
        }

        public void MarkMeasurementSent(IList<long> ids)
        {
            UpdateByIds(ids, "UPDATE controller_measurement_outbox SET status='sent', sent_at=NOW(), last_error=NULL WHERE id = ANY(@ids)", null);
        }

        public void MarkMeasurementDead(IList<long> ids, string error)
        {
            UpdateByIds(ids, "UPDATE controller_measurement_outbox SET status='dead', last_error=@error WHERE id = ANY(@ids)",
                delegate(NpgsqlCommand cmd) { AddText(cmd, "error", error ?? "permanent_error"); });
        }

        public void MarkMeasurementFailed(IList<long> ids, string error, int attempts)
        {
            var delaySeconds = RetryDelaySeconds(attempts);
            UpdateByIds(ids,
                "UPDATE controller_measurement_outbox SET attempts=attempts+1, last_error=@error, next_attempt_at=NOW()+make_interval(secs => @delay_seconds) WHERE id = ANY(@ids)",
                delegate(NpgsqlCommand cmd)
                {
                    AddText(cmd, "error", error ?? "measurement_push_failed");
                    cmd.Parameters.AddWithValue("delay_seconds", delaySeconds);
                });
        }

        public void CleanupSent(int retentionDays)
        {
            var days = Math.Max(1, retentionDays);
            using (var conn = Open())
            using (var tx = conn.BeginTransaction())
            {
                var total = 0;
                using (var parking = new NpgsqlCommand("DELETE FROM controller_parking_outbox WHERE status='sent' AND sent_at < NOW() - make_interval(days => @days)", conn, tx))
                {
                    parking.Parameters.AddWithValue("days", days);
                    total += parking.ExecuteNonQuery();
                }
                using (var measurement = new NpgsqlCommand("DELETE FROM controller_measurement_outbox WHERE status='sent' AND sent_at < NOW() - make_interval(days => @days)", conn, tx))
                {
                    measurement.Parameters.AddWithValue("days", days);
                    total += measurement.ExecuteNonQuery();
                }
                tx.Commit();
                if (total > 0 && _logger != null) _logger.Info("db-cleanup", "Removed sent outbox rows", "count=" + total);
            }
        }

        private static int RetryDelaySeconds(int attempts)
        {
            return Math.Min(60, Math.Max(2, (int)Math.Pow(2, Math.Min(5, Math.Max(1, attempts)))));
        }

        private void UpdateByIds(IList<long> ids, string sql, Action<NpgsqlCommand> addParameters)
        {
            if (ids == null || ids.Count == 0) return;
            using (var conn = Open())
            using (var cmd = new NpgsqlCommand(sql, conn))
            {
                cmd.Parameters.Add("ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint).Value = ids.ToArray();
                if (addParameters != null) addParameters(cmd);
                cmd.ExecuteNonQuery();
            }
        }

        private void ExecuteNonQuery(string sql)
        {
            using (var conn = Open())
            using (var cmd = new NpgsqlCommand(sql, conn)) cmd.ExecuteNonQuery();
        }

        private NpgsqlConnection Open()
        {
            var conn = new NpgsqlConnection(_connectionString);
            conn.Open();
            return conn;
        }

        private static IDictionary<string, string> ToCaseInsensitiveOptions(string json)
        {
            var parsed = JsonConvert.DeserializeObject<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
            return new Dictionary<string, string>(parsed, StringComparer.OrdinalIgnoreCase);
        }

        private static void AddText(NpgsqlCommand cmd, string name, string value)
        {
            var parameter = cmd.Parameters.Add(name, NpgsqlDbType.Text);
            parameter.Value = string.IsNullOrWhiteSpace(value) ? (object)DBNull.Value : value.Trim();
        }

        private static string GetNullableString(NpgsqlDataReader reader, int ordinal)
        {
            return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
        }

        private static DateTime NormalizeUtc(DateTime value)
        {
            if (value.Kind == DateTimeKind.Utc) return value;
            if (value.Kind == DateTimeKind.Unspecified) return DateTime.SpecifyKind(value, DateTimeKind.Utc);
            return value.ToUniversalTime();
        }
    }
}
