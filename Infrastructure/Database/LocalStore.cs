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

        public void UpsertReaderConfig(ReaderDeviceConfig config)
        {
            if (config == null || string.IsNullOrWhiteSpace(config.SerialNumber)) return;
            const string sql = @"
INSERT INTO controller_reader
(serial_number, driver_key, endpoint, port, enabled, config_hash,
 power_dbm, read_interval_ms, tid_start_address, tid_length, options_json, updated_at)
VALUES
(@serial_number, @driver_key, @endpoint, @port, @enabled, @config_hash,
 @power_dbm, @read_interval_ms, @tid_start_address, @tid_length, CAST(@options_json AS jsonb), NOW())
ON CONFLICT (serial_number) DO UPDATE SET
 driver_key=EXCLUDED.driver_key,
 endpoint=EXCLUDED.endpoint,
 port=EXCLUDED.port,
 enabled=EXCLUDED.enabled,
 config_hash=EXCLUDED.config_hash,
 power_dbm=EXCLUDED.power_dbm,
 read_interval_ms=EXCLUDED.read_interval_ms,
 tid_start_address=EXCLUDED.tid_start_address,
 tid_length=EXCLUDED.tid_length,
 options_json=EXCLUDED.options_json,
 updated_at=NOW();";

            using (var conn = Open())
            using (var cmd = new NpgsqlCommand(sql, conn))
            {
                AddText(cmd, "serial_number", config.SerialNumber.Trim().ToUpperInvariant());
                AddText(cmd, "driver_key", config.DriverKey ?? string.Empty);
                AddText(cmd, "endpoint", config.Endpoint);
                cmd.Parameters.AddWithValue("port", config.Port);
                cmd.Parameters.AddWithValue("enabled", config.Enabled);
                AddText(cmd, "config_hash", config.ConfigHash);
                cmd.Parameters.AddWithValue("power_dbm", config.PowerDbm);
                cmd.Parameters.AddWithValue("read_interval_ms", config.ReadIntervalMs);
                cmd.Parameters.AddWithValue("tid_start_address", config.TidStartAddress);
                cmd.Parameters.AddWithValue("tid_length", config.TidLength);
                cmd.Parameters.AddWithValue("options_json", JsonConvert.SerializeObject(config.Options ?? new Dictionary<string, string>()));
                cmd.ExecuteNonQuery();
            }
        }

        public void DisableReadersNotIn(IList<string> serialNumbers)
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

        public IList<ReaderDeviceConfig> GetReaderConfigs()
        {
            const string sql = @"
SELECT serial_number, driver_key, endpoint, port, enabled, config_hash,
       power_dbm, read_interval_ms, tid_start_address, tid_length, options_json::text
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
                        SerialNumber = serial,
                        DriverKey = GetNullableString(reader, 1),
                        Endpoint = GetNullableString(reader, 2),
                        Port = reader.GetInt32(3),
                        Enabled = reader.GetBoolean(4),
                        ConfigHash = GetNullableString(reader, 5),
                        PowerDbm = reader.GetInt32(6),
                        ReadIntervalMs = reader.GetInt32(7),
                        TidStartAddress = reader.GetInt32(8),
                        TidLength = reader.GetInt32(9),
                        Options = ToCaseInsensitiveOptions(reader.GetString(10))
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
(serial_number, detected_sdk_serial, detected_endpoint, driver_key, model, endpoint, online, message, firmware_version,
 power_dbm, read_interval_ms, tid_start_address, tid_length, configuration_applied, configuration_source,
 applied_config_hash, configuration_applied_at, ports_json, updated_at)
VALUES
(@serial_number, @detected_sdk_serial, @detected_endpoint, @driver_key, @model, @endpoint, @online, @message, @firmware_version,
 @power_dbm, @read_interval_ms, @tid_start_address, @tid_length, @configuration_applied, @configuration_source,
 @applied_config_hash, @configuration_applied_at, CAST(@ports_json AS jsonb), @updated_at)
ON CONFLICT (serial_number) DO UPDATE SET
 detected_sdk_serial=EXCLUDED.detected_sdk_serial,
 detected_endpoint=EXCLUDED.detected_endpoint,
 driver_key=EXCLUDED.driver_key,
 model=EXCLUDED.model,
 endpoint=EXCLUDED.endpoint,
 online=EXCLUDED.online,
 message=EXCLUDED.message,
 firmware_version=EXCLUDED.firmware_version,
 power_dbm=EXCLUDED.power_dbm,
 read_interval_ms=EXCLUDED.read_interval_ms,
 tid_start_address=EXCLUDED.tid_start_address,
 tid_length=EXCLUDED.tid_length,
 configuration_applied=EXCLUDED.configuration_applied,
 configuration_source=EXCLUDED.configuration_source,
 applied_config_hash=EXCLUDED.applied_config_hash,
 configuration_applied_at=EXCLUDED.configuration_applied_at,
 ports_json=EXCLUDED.ports_json,
 updated_at=EXCLUDED.updated_at;";
            using (var conn = Open())
            using (var cmd = new NpgsqlCommand(sql, conn))
            {
                AddText(cmd, "serial_number", status.SerialNumber.Trim().ToUpperInvariant());
                AddText(cmd, "detected_sdk_serial", status.DetectedSdkSerialNumber);
                AddText(cmd, "detected_endpoint", status.DetectedEndpoint);
                AddText(cmd, "driver_key", status.DriverKey);
                AddText(cmd, "model", status.Model);
                AddText(cmd, "endpoint", status.Endpoint);
                cmd.Parameters.AddWithValue("online", status.Online);
                AddText(cmd, "message", status.Message);
                AddText(cmd, "firmware_version", status.FirmwareVersion);
                cmd.Parameters.AddWithValue("power_dbm", Math.Max(0, Math.Min(40, status.PowerDbm)));
                cmd.Parameters.AddWithValue("read_interval_ms", Math.Max(0, Math.Min(60000, status.ReadIntervalMs)));
                cmd.Parameters.AddWithValue("tid_start_address", Math.Max(0, status.TidStartAddress));
                cmd.Parameters.AddWithValue("tid_length", Math.Max(0, status.TidLength));
                cmd.Parameters.AddWithValue("configuration_applied", status.ConfigurationApplied);
                AddText(cmd, "configuration_source", status.ConfigurationSource);
                AddText(cmd, "applied_config_hash", status.AppliedConfigHash);
                cmd.Parameters.AddWithValue("configuration_applied_at", NpgsqlDbType.TimestampTz,
                    status.ConfigurationAppliedAtUtc.HasValue ? (object)NormalizeUtc(status.ConfigurationAppliedAtUtc.Value) : DBNull.Value);
                cmd.Parameters.AddWithValue("ports_json", JsonConvert.SerializeObject((status.Ports ?? new List<int>()).Where(value => value >= 1 && value <= 16).Distinct().OrderBy(value => value)));
                cmd.Parameters.AddWithValue("updated_at", NormalizeUtc(status.UpdatedAtUtc));
                cmd.ExecuteNonQuery();
            }
        }

        public void MarkReaderRuntimeStatusesOffline()
        {
            ExecuteNonQuery(
                "UPDATE controller_reader_runtime_status "
                + "SET online=FALSE, message='controller_restarted';");
        }

        public IList<ReaderStatus> GetReaderStatuses()
        {
            const string sql = @"
SELECT s.serial_number, s.detected_sdk_serial, s.detected_endpoint, s.driver_key, s.model, s.endpoint,
       s.online, s.message, s.firmware_version, s.power_dbm, s.read_interval_ms,
       s.tid_start_address, s.tid_length, s.configuration_applied, s.configuration_source,
       s.applied_config_hash, s.configuration_applied_at, s.ports_json::text, s.updated_at
  FROM controller_reader_runtime_status s
 WHERE NULLIF(BTRIM(s.detected_sdk_serial), '') IS NOT NULL
 ORDER BY s.detected_sdk_serial, s.updated_at DESC";
            var result = new List<ReaderStatus>();
            using (var conn = Open())
            using (var cmd = new NpgsqlCommand(sql, conn))
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    result.Add(new ReaderStatus
                    {
                        SerialNumber = reader.GetString(0),
                        DetectedSdkSerialNumber = GetNullableString(reader, 1),
                        DetectedEndpoint = GetNullableString(reader, 2),
                        DriverKey = GetNullableString(reader, 3),
                        Model = GetNullableString(reader, 4),
                        Endpoint = GetNullableString(reader, 5),
                        Online = reader.GetBoolean(6),
                        Message = GetNullableString(reader, 7),
                        FirmwareVersion = GetNullableString(reader, 8),
                        PowerDbm = reader.GetInt32(9),
                        ReadIntervalMs = reader.GetInt32(10),
                        TidStartAddress = reader.GetInt32(11),
                        TidLength = reader.GetInt32(12),
                        ConfigurationApplied = reader.GetBoolean(13),
                        ConfigurationSource = GetNullableString(reader, 14),
                        AppliedConfigHash = GetNullableString(reader, 15),
                        ConfigurationAppliedAtUtc = reader.IsDBNull(16) ? (DateTime?)null : reader.GetDateTime(16).ToUniversalTime(),
                        Ports = JsonConvert.DeserializeObject<List<int>>(reader.GetString(17)) ?? new List<int>(),
                        UpdatedAtUtc = reader.GetDateTime(18).ToUniversalTime()
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
(event_uid, serial_number, port_no, tid, detected_at)
VALUES
(@event_uid, @serial_number, @port_no, @tid, @detected_at)
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
                    AddText(cmd, "serial_number", detection.SerialNumber);
                    cmd.Parameters.AddWithValue("port_no", detection.PortNo);
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
SELECT id, event_uid, serial_number, port_no, tid, detected_at, attempts
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
                                SerialNumber = reader.GetString(2),
                                PortNo = reader.GetInt32(3),
                                Tid = reader.GetString(4),
                                DetectedAtUtc = reader.GetDateTime(5).ToUniversalTime()
                            },
                            Attempts = reader.GetInt32(6)
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

        public int RequeueGatewayConfigurationFailures()
        {
            // 1.4.22 treated every HTTP 400 (except explicit transient codes) as
            // permanent. t4_coreapi can return HTTP 400 when an existing Gateway
            // route has lost its Server Action, which is a server deployment fault.
            // Recover only that exact class of dead record; payload-validation 400s
            // remain dead and require operator review.
            const string sql = @"
UPDATE controller_parking_outbox
   SET status='pending',
       attempts=0,
       next_attempt_at=NOW(),
       last_error=NULL,
       sent_at=NULL
 WHERE status='dead'
   AND last_error ILIKE '%no Server Action configured%'";
            using (var conn = Open())
            using (var cmd = new NpgsqlCommand(sql, conn))
                return cmd.ExecuteNonQuery();
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

        public void EnqueueLaneCalibrationEvents(IList<LaneCalibrationEvent> events)
        {
            if (events == null || events.Count == 0) return;
            const string sql = @"
INSERT INTO controller_lane_calibration_outbox
(event_uid, lane_calibration_code, revision, power_dbm, read_interval_ms, serial_number, port_no, tid, rssi_dbm, read_at)
VALUES
(@event_uid, @lane_calibration_code, @revision, @power_dbm, @read_interval_ms, @serial_number, @port_no, @tid, @rssi_dbm, @read_at)
ON CONFLICT (event_uid) DO NOTHING;";
            using (var conn = Open())
            using (var tx = conn.BeginTransaction())
            using (var cmd = new NpgsqlCommand(sql, conn, tx))
            {
                foreach (var evt in events)
                {
                    if (evt == null || string.IsNullOrWhiteSpace(evt.EventUid) || string.IsNullOrWhiteSpace(evt.LaneCalibrationCode)) continue;
                    cmd.Parameters.Clear();
                    AddText(cmd, "event_uid", evt.EventUid);
                    AddText(cmd, "lane_calibration_code", evt.LaneCalibrationCode);
                    cmd.Parameters.AddWithValue("revision", Math.Max(1, evt.Revision));
                    cmd.Parameters.AddWithValue("power_dbm", Math.Max(0, evt.PowerDbm));
                    cmd.Parameters.AddWithValue("read_interval_ms", Math.Max(1, Math.Min(60000, evt.ReadIntervalMs)));
                    AddText(cmd, "serial_number", evt.SerialNumber);
                    cmd.Parameters.AddWithValue("port_no", evt.PortNo);
                    AddText(cmd, "tid", evt.Tid);
                    var rssi = cmd.Parameters.Add("rssi_dbm", NpgsqlDbType.Double);
                    rssi.Value = evt.RssiDbm.HasValue ? (object)evt.RssiDbm.Value : DBNull.Value;
                    cmd.Parameters.AddWithValue("read_at", NormalizeUtc(evt.ReadAtUtc));
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }
        }

        public IList<LaneCalibrationOutboxItem> GetPendingLaneCalibrationEvents(int limit)
        {
            const string sql = @"
SELECT id, event_uid, lane_calibration_code, revision, power_dbm, read_interval_ms, serial_number, port_no, tid, rssi_dbm, read_at, attempts
  FROM controller_lane_calibration_outbox
 WHERE status='pending' AND next_attempt_at <= NOW()
 ORDER BY id
 LIMIT @limit";
            var result = new List<LaneCalibrationOutboxItem>();
            using (var conn = Open())
            using (var cmd = new NpgsqlCommand(sql, conn))
            {
                cmd.Parameters.AddWithValue("limit", Math.Max(1, Math.Min(100, limit)));
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        result.Add(new LaneCalibrationOutboxItem
                        {
                            Id = reader.GetInt64(0),
                            Event = new LaneCalibrationEvent
                            {
                                EventUid = reader.GetString(1),
                                LaneCalibrationCode = reader.GetString(2),
                                Revision = reader.GetInt32(3),
                                PowerDbm = reader.GetInt32(4),
                                ReadIntervalMs = reader.GetInt32(5),
                                SerialNumber = reader.GetString(6),
                                PortNo = reader.GetInt32(7),
                                Tid = reader.GetString(8),
                                RssiDbm = reader.IsDBNull(9) ? (double?)null : reader.GetDouble(9),
                                ReadAtUtc = reader.GetDateTime(10).ToUniversalTime()
                            },
                            Attempts = reader.GetInt32(11)
                        });
                    }
                }
            }
            return result;
        }

        public void MarkLaneCalibrationSent(IList<long> ids)
        {
            UpdateByIds(ids, "UPDATE controller_lane_calibration_outbox SET status='sent', sent_at=NOW(), last_error=NULL WHERE id = ANY(@ids)", null);
        }

        public void MarkLaneCalibrationFailed(IList<long> ids, string error, int attempts)
        {
            var delaySeconds = RetryDelaySeconds(attempts);
            UpdateByIds(ids,
                "UPDATE controller_lane_calibration_outbox SET attempts=attempts+1, last_error=@error, next_attempt_at=NOW()+make_interval(secs => @delay_seconds) WHERE id = ANY(@ids)",
                delegate(NpgsqlCommand cmd)
                {
                    AddText(cmd, "error", error ?? "lane_calibration_push_failed");
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
                using (var calibration = new NpgsqlCommand("DELETE FROM controller_lane_calibration_outbox WHERE status='sent' AND sent_at < NOW() - make_interval(days => @days)", conn, tx))
                {
                    calibration.Parameters.AddWithValue("days", days);
                    total += calibration.ExecuteNonQuery();
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
