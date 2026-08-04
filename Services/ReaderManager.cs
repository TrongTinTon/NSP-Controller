using System;
using System.Collections.Generic;
using System.Linq;
using NSPGatekeeper.Controller.Configuration;
using NSPGatekeeper.Controller.Domain;
using NSPGatekeeper.Controller.Infrastructure.Database;
using NSPGatekeeper.Controller.Infrastructure.Logging;
using NSPGatekeeper.Controller.Readers;

namespace NSPGatekeeper.Controller.Services
{
    public sealed class ReaderManager : IDisposable
    {
        private readonly ReaderDriverRegistry _registry;
        private readonly LocalStore _store;
        private readonly FileLogger _logger;
        private readonly AppSettings _settings;
        private readonly DetectionOutboxWriter _outbox;
        private readonly object _gate = new object();
        private readonly object _applyGate = new object();
        private readonly Dictionary<string, RuntimeHandle> _runtimes = new Dictionary<string, RuntimeHandle>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _lastStatusSignatures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Queue<RfidDetection> _recentDetections = new Queue<RfidDetection>();
        private LaneCalibrationSessionConfig _laneCalibration;
        private HashSet<string> _laneCalibrationReaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool _disposed;

        public ReaderManager(
            ReaderDriverRegistry registry,
            LocalStore store,
            DetectionOutboxWriter outbox,
            FileLogger logger,
            AppSettings settings)
        {
            _registry = registry ?? throw new ArgumentNullException("registry");
            _store = store ?? throw new ArgumentNullException("store");
            _outbox = outbox ?? throw new ArgumentNullException("outbox");
            _logger = logger;
            _settings = settings ?? throw new ArgumentNullException("settings");
        }

        public string CurrentLaneCalibrationCode
        {
            get
            {
                lock (_gate)
                    return _laneCalibration == null
                        ? string.Empty
                        : (_laneCalibration.LaneCalibrationCode ?? string.Empty);
            }
        }

        public string CurrentMode
        {
            get
            {
                lock (_gate)
                    return _laneCalibration != null && _laneCalibration.IsRunningDesired
                        ? "Lane Calibration"
                        : "Parking";
            }
        }

        public void StartCachedConfiguration()
        {
            var configs = _store.GetReaderConfigs();
            if (_logger != null)
                _logger.Info("reader-config", "Starting cached Reader configuration", "count=" + configs.Count);
            ApplyRuntimeConfiguration(configs);
        }

        public void ApplyServerConfiguration(IList<ReaderDeviceConfig> serverConfigs)
        {
            serverConfigs = serverConfigs ?? new List<ReaderDeviceConfig>();
            var cached = _store.GetReaderConfigs()
                .Where(config => config != null && !string.IsNullOrWhiteSpace(config.SerialNumber))
                .ToDictionary(
                    config => NormalizeSerial(config.SerialNumber),
                    StringComparer.OrdinalIgnoreCase);

            var merged = new List<ReaderDeviceConfig>();
            foreach (var incoming in serverConfigs)
            {
                if (incoming == null || string.IsNullOrWhiteSpace(incoming.SerialNumber)) continue;

                ReaderDeviceConfig local;
                cached.TryGetValue(NormalizeSerial(incoming.SerialNumber), out local);
                var config = MergePhysicalProfile(incoming, local);
                merged.Add(config);
                _store.UpsertReaderConfig(config);

                if (_logger != null)
                    _logger.Info("reader-config", "Reader configuration prepared", DescribeConfig(config));
            }

            _store.DisableReadersNotIn(merged.Select(config => config.SerialNumber).ToList());
            ApplyRuntimeConfiguration(BuildEffectiveReaderConfigs(merged, CurrentLaneCalibration()));
        }

        public void ApplyLaneCalibrationConfiguration(LaneCalibrationSessionConfig config)
        {
            if (config == null || !config.IsRunningDesired || string.IsNullOrWhiteSpace(config.LaneCalibrationCode))
            {
                ClearLaneCalibration("server_stopped");
                return;
            }

            config.LaneCalibrationCode = NormalizeSerial(config.LaneCalibrationCode);
            if (config.Revision <= 0) config.Revision = 1;
            config.Readers = (config.Readers ?? new List<LaneCalibrationReaderConfig>())
                .Where(reader => reader != null && !string.IsNullOrWhiteSpace(reader.SerialNumber))
                .Select(reader => new LaneCalibrationReaderConfig
                {
                    SerialNumber = NormalizeSerial(reader.SerialNumber),
                    PowerDbm = Math.Max(0, Math.Min(40, reader.PowerDbm)),
                    ReadIntervalMs = Math.Max(1, Math.Min(60000, reader.ReadIntervalMs))
                })
                .GroupBy(reader => reader.SerialNumber, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(reader => reader.SerialNumber, StringComparer.OrdinalIgnoreCase)
                .ToList();

            ValidateLaneCalibrationReaders(config);

            bool changed;
            lock (_gate)
            {
                changed = _laneCalibration == null
                    || !string.Equals(_laneCalibration.LaneCalibrationCode, config.LaneCalibrationCode, StringComparison.OrdinalIgnoreCase)
                    || _laneCalibration.Revision != config.Revision
                    || !string.Equals(
                        LaneCalibrationReaderSignature(_laneCalibration),
                        LaneCalibrationReaderSignature(config),
                        StringComparison.Ordinal);

                _laneCalibration = config;
                _laneCalibrationReaders = new HashSet<string>(
                    config.Readers.Select(reader => reader.SerialNumber),
                    StringComparer.OrdinalIgnoreCase);
            }

            if (!changed) return;

            ApplyRuntimeConfiguration(BuildEffectiveReaderConfigs(_store.GetReaderConfigs(), config));
            if (_logger != null)
                _logger.Info(
                    "lane-calibration",
                    "Lane Calibration runtime applied",
                    "code=" + config.LaneCalibrationCode
                    + "; revision=" + config.Revision
                    + "; readers=" + config.Readers.Count
                    + "; port_filtering=server");
        }

        public void ClearLaneCalibration(string reason)
        {
            bool cleared;
            lock (_gate) cleared = ClearLaneCalibrationLocked(reason);
            if (cleared && !_disposed)
                ApplyRuntimeConfiguration(_store.GetReaderConfigs());
        }

        public IList<ReaderStatus> GetStatuses()
        {
            return _store.GetReaderStatuses();
        }

        public IList<RfidDetection> GetRecentDetections()
        {
            lock (_gate) return _recentDetections.ToList();
        }

        private void ValidateLaneCalibrationReaders(LaneCalibrationSessionConfig config)
        {
            if (config.Readers.Count == 0)
                throw new InvalidOperationException("Lane Calibration has no Reader configuration.");

            var configuredReaders = _store.GetReaderConfigs()
                .Where(reader => reader != null && !string.IsNullOrWhiteSpace(reader.SerialNumber))
                .ToDictionary(
                    reader => NormalizeSerial(reader.SerialNumber),
                    StringComparer.OrdinalIgnoreCase);
            var configuredSerials = configuredReaders.Count == 0
                ? "none"
                : string.Join(",", configuredReaders.Keys.OrderBy(value => value, StringComparer.OrdinalIgnoreCase));

            foreach (var calibrationReader in config.Readers)
            {
                ReaderDeviceConfig reader;
                if (!configuredReaders.TryGetValue(calibrationReader.SerialNumber, out reader))
                    throw new ReaderConfigurationUnavailableException(
                        calibrationReader.SerialNumber,
                        "Lane Calibration is waiting for Reader configuration: "
                        + calibrationReader.SerialNumber
                        + ". Configured Readers: " + configuredSerials + ".");

                if (!reader.Enabled)
                    throw new ReaderConfigurationUnavailableException(
                        calibrationReader.SerialNumber,
                        "Lane Calibration is waiting for an enabled Reader: "
                        + calibrationReader.SerialNumber + ".");

                var maximumPower = MaximumPower(reader.DriverKey);
                if (calibrationReader.PowerDbm > maximumPower)
                    throw new InvalidOperationException(
                        "Lane Calibration power exceeds Reader driver capability: "
                        + calibrationReader.SerialNumber
                        + "; requested=" + calibrationReader.PowerDbm
                        + "; maximum=" + maximumPower);
            }
        }

        private ReaderDeviceConfig MergePhysicalProfile(ReaderDeviceConfig incoming, ReaderDeviceConfig local)
        {
            incoming.SerialNumber = NormalizeSerial(incoming.SerialNumber);
            incoming.Options = incoming.Options
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (local != null)
            {
                if (string.IsNullOrWhiteSpace(incoming.DriverKey)) incoming.DriverKey = local.DriverKey;

                // COM/IP binding is physical Controller state and is never owned by Cloud/Edge.
                incoming.Endpoint = local.Endpoint;
                incoming.Port = local.Port;

                if (local.Options != null)
                {
                    foreach (var pair in local.Options)
                        if (!incoming.Options.ContainsKey(pair.Key)) incoming.Options[pair.Key] = pair.Value;
                }
            }

            if (string.IsNullOrWhiteSpace(incoming.DriverKey)) incoming.DriverKey = "cf-e718";
            incoming.DriverKey = incoming.DriverKey.Trim().ToLowerInvariant();
            incoming.PowerDbm = NormalizePower(incoming.DriverKey, incoming.PowerDbm);

            if (string.IsNullOrWhiteSpace(incoming.Endpoint)
                && string.Equals(incoming.DriverKey, "cf-e718", StringComparison.OrdinalIgnoreCase)
                && !incoming.Options.ContainsKey("connection"))
            {
                incoming.Options["connection"] = "com";
            }

            // Reader lifecycle depends only on server identity/enabled state.
            // Port selection is intentionally not a Controller concern.
            return incoming;
        }

        private void ApplyRuntimeConfiguration(IList<ReaderDeviceConfig> configs)
        {
            ThrowIfDisposed();
            configs = configs ?? new List<ReaderDeviceConfig>();
            var desired = configs
                .Where(config => config != null && !string.IsNullOrWhiteSpace(config.SerialNumber))
                .GroupBy(config => NormalizeSerial(config.SerialNumber), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);

            lock (_applyGate)
            {
                var stopped = new List<RuntimeStopRequest>();
                lock (_gate)
                {
                    foreach (var serial in _runtimes.Keys.ToList())
                    {
                        ReaderDeviceConfig next;
                        desired.TryGetValue(serial, out next);
                        var reason = RestartReason(_runtimes[serial].Config, next);
                        if (string.IsNullOrWhiteSpace(reason)) continue;

                        stopped.Add(new RuntimeStopRequest(serial, _runtimes[serial], reason));
                        _runtimes.Remove(serial);
                    }
                }

                foreach (var request in stopped)
                {
                    if (_logger != null)
                        _logger.Info(
                            "reader-manager",
                            "Reader runtime stop/restart requested",
                            "serial=" + request.SerialNumber + "; reason=" + request.Reason);
                    StopRuntimeHandle(request.SerialNumber, request.Handle);
                }

                foreach (var config in desired.Values.Where(value => value.Enabled))
                {
                    var serial = NormalizeSerial(config.SerialNumber);
                    lock (_gate)
                    {
                        if (_runtimes.ContainsKey(serial)) continue;
                    }

                    IReaderRuntime runtime = null;
                    try
                    {
                        runtime = _registry.Create(config);
                        runtime.DetectionReceived += OnDetection;
                        runtime.StatusChanged += OnStatus;

                        lock (_gate)
                        {
                            if (_runtimes.ContainsKey(serial))
                            {
                                runtime.DetectionReceived -= OnDetection;
                                runtime.StatusChanged -= OnStatus;
                                runtime.Dispose();
                                continue;
                            }

                            _runtimes[serial] = new RuntimeHandle(config, runtime);
                        }

                        runtime.Start();
                        if (_logger != null)
                            _logger.Info("reader-manager", "Reader runtime started", DescribeConfig(config));
                    }
                    catch (Exception ex)
                    {
                        lock (_gate)
                        {
                            RuntimeHandle current;
                            if (_runtimes.TryGetValue(serial, out current)
                                && ReferenceEquals(current.Runtime, runtime))
                            {
                                _runtimes.Remove(serial);
                            }
                        }

                        if (runtime != null)
                        {
                            runtime.DetectionReceived -= OnDetection;
                            runtime.StatusChanged -= OnStatus;
                            try { runtime.Dispose(); }
                            catch (Exception disposeError)
                            {
                                if (_logger != null)
                                    _logger.Error(
                                        "reader-manager",
                                        "Reader runtime cleanup failed after start error",
                                        disposeError,
                                        "serial=" + serial);
                            }
                        }

                        if (_logger != null)
                            _logger.Error("reader-manager", "Reader runtime start failed: " + serial, ex);
                        PersistStoppedStatus(config, ex.Message);
                    }
                }
            }
        }

        private void OnDetection(RfidDetection detection)
        {
            if (detection == null
                || string.IsNullOrWhiteSpace(detection.SerialNumber)
                || detection.PortNo <= 0
                || string.IsNullOrWhiteSpace(detection.Tid))
            {
                return;
            }

            detection.SerialNumber = NormalizeSerial(detection.SerialNumber);
            detection.Tid = detection.Tid.Trim().ToUpperInvariant();
            detection.EventUid = BuildEventUid("RFID");

            LaneCalibrationSessionConfig laneCalibration;
            ReaderDeviceConfig appliedConfig = null;
            bool laneCalibrationReader;
            lock (_gate)
            {
                laneCalibration = _laneCalibration;
                laneCalibrationReader = laneCalibration != null
                    && laneCalibration.IsRunningDesired
                    && _laneCalibrationReaders.Contains(detection.SerialNumber);

                RuntimeHandle handle;
                if (_runtimes.TryGetValue(detection.SerialNumber, out handle))
                    appliedConfig = handle.Config;

                _recentDetections.Enqueue(detection);
                while (_recentDetections.Count > 500) _recentDetections.Dequeue();
            }

            try
            {
                if (!laneCalibrationReader)
                {
                    _outbox.EnqueueParking(detection);
                    return;
                }

                var requestedConfig = laneCalibration.Reader(detection.SerialNumber);
                _outbox.EnqueueLaneCalibration(new LaneCalibrationEvent
                {
                    EventUid = BuildEventUid("CAL"),
                    LaneCalibrationCode = laneCalibration.LaneCalibrationCode,
                    Revision = laneCalibration.Revision,
                    PowerDbm = appliedConfig == null
                        ? (requestedConfig == null ? 0 : requestedConfig.PowerDbm)
                        : appliedConfig.PowerDbm,
                    ReadIntervalMs = appliedConfig == null
                        ? (requestedConfig == null ? 200 : requestedConfig.ReadIntervalMs)
                        : appliedConfig.ReadIntervalMs,
                    SerialNumber = detection.SerialNumber,
                    PortNo = detection.PortNo,
                    Tid = detection.Tid,
                    RssiDbm = detection.RssiDbm,
                    ReadAtUtc = detection.DetectedAtUtc
                });
            }
            catch (Exception ex)
            {
                if (_logger != null)
                    _logger.Error(
                        "rfid-outbox",
                        "Could not queue raw RFID event",
                        ex,
                        "serial=" + detection.SerialNumber
                        + "; port_no=" + detection.PortNo
                        + "; tid=" + detection.Tid);
            }
        }

        private void OnStatus(ReaderStatus status)
        {
            if (status == null) return;
            var serial = NormalizeSerial(status.SerialNumber);
            if (string.IsNullOrWhiteSpace(serial)) return;

            string statusSignature;
            string previousEndpoint = null;
            string discoveredEndpoint = null;
            string driverKey = null;
            int tcpPort = 0;

            lock (_gate)
            {
                RuntimeHandle handle;
                if (_runtimes.TryGetValue(serial, out handle))
                {
                    status.SerialNumber = serial;
                    status.PowerDbm = handle.Config.PowerDbm;
                    status.ReadIntervalMs = handle.Config.ReadIntervalMs;

                    if (status.Online
                        && IsComEndpoint(status.Endpoint)
                        && !string.Equals(handle.Config.Endpoint, status.Endpoint, StringComparison.OrdinalIgnoreCase))
                    {
                        previousEndpoint = handle.Config.Endpoint;
                        discoveredEndpoint = status.Endpoint.Trim().ToUpperInvariant();
                        driverKey = handle.Config.DriverKey;
                        tcpPort = handle.Config.Port;
                        handle.Config.Endpoint = discoveredEndpoint;
                        if (handle.Config.Options != null)
                            handle.Config.Options["connection"] = "com";
                    }
                }

                status.Ports = (status.Ports ?? new List<int>())
                    .Where(port => port >= 1 && port <= 16)
                    .Distinct()
                    .OrderBy(port => port)
                    .ToList();

                statusSignature = status.Online
                    + "|" + (status.Message ?? string.Empty)
                    + "|" + (status.Endpoint ?? string.Empty)
                    + "|" + (status.DetectedSdkSerialNumber ?? string.Empty)
                    + "|" + (status.DetectedEndpoint ?? string.Empty);

                string previous;
                if (!_lastStatusSignatures.TryGetValue(serial, out previous)
                    || !string.Equals(previous, statusSignature, StringComparison.Ordinal))
                {
                    _lastStatusSignatures[serial] = statusSignature;
                    if (_logger != null)
                        _logger.Info(
                            "reader-status",
                            "Reader runtime status changed",
                            "configured_serial=" + serial
                            + "; detected_sdk_serial=" + (status.DetectedSdkSerialNumber ?? string.Empty)
                            + "; detected_endpoint=" + (status.DetectedEndpoint ?? string.Empty)
                            + "; online=" + status.Online
                            + "; message=" + (status.Message ?? string.Empty)
                            + "; endpoint=" + (status.Endpoint ?? string.Empty)
                            + "; scan_ports=" + string.Join(",", status.Ports));
                }
            }

            if (!string.IsNullOrWhiteSpace(discoveredEndpoint))
            {
                try
                {
                    _store.UpdateLocalReaderConnection(serial, driverKey, discoveredEndpoint, tcpPort);
                    if (_logger != null)
                        _logger.Info(
                            "reader-binding",
                            "Reader physical COM binding updated from verified SDK identity",
                            "serial=" + serial
                            + "; previous=" + (string.IsNullOrWhiteSpace(previousEndpoint) ? "AUTO-COM" : previousEndpoint)
                            + "; current=" + discoveredEndpoint);
                }
                catch (Exception ex)
                {
                    if (_logger != null)
                        _logger.Error(
                            "reader-binding",
                            "Could not persist discovered Reader COM binding",
                            ex,
                            "serial=" + serial + "; endpoint=" + discoveredEndpoint);
                }
            }

            try
            {
                _store.UpsertReaderStatus(status);
            }
            catch (Exception ex)
            {
                if (_logger != null)
                    _logger.Error("reader-status", "Could not persist Reader status", ex, "serial=" + serial);
            }
        }

        private IList<ReaderDeviceConfig> BuildEffectiveReaderConfigs(
            IList<ReaderDeviceConfig> baseConfigs,
            LaneCalibrationSessionConfig laneCalibration)
        {
            baseConfigs = baseConfigs ?? new List<ReaderDeviceConfig>();
            if (laneCalibration == null || !laneCalibration.IsRunningDesired) return baseConfigs;

            var selectedReaders = (laneCalibration.Readers ?? new List<LaneCalibrationReaderConfig>())
                .Where(reader => reader != null && !string.IsNullOrWhiteSpace(reader.SerialNumber))
                .ToDictionary(
                    reader => NormalizeSerial(reader.SerialNumber),
                    reader => reader,
                    StringComparer.OrdinalIgnoreCase);

            return baseConfigs
                .Where(config => config != null)
                .Select(config =>
                {
                    LaneCalibrationReaderConfig calibrationReader;
                    if (!selectedReaders.TryGetValue(NormalizeSerial(config.SerialNumber), out calibrationReader))
                        return config;

                    var clone = CloneReaderConfig(config);
                    clone.PowerDbm = NormalizePower(clone.DriverKey, calibrationReader.PowerDbm);
                    clone.ReadIntervalMs = Math.Max(1, Math.Min(60000, calibrationReader.ReadIntervalMs));
                    clone.ConfigHash = (config.ConfigHash ?? string.Empty)
                        + "|CAL|" + laneCalibration.LaneCalibrationCode
                        + "|R" + laneCalibration.Revision
                        + "|P" + clone.PowerDbm
                        + "|I" + clone.ReadIntervalMs;
                    return clone;
                })
                .ToList();
        }

        private static ReaderDeviceConfig CloneReaderConfig(ReaderDeviceConfig source)
        {
            var clone = new ReaderDeviceConfig
            {
                DriverKey = source.DriverKey,
                SerialNumber = source.SerialNumber,
                Endpoint = source.Endpoint,
                Port = source.Port,
                Enabled = source.Enabled,
                ConfigHash = source.ConfigHash,
                PowerDbm = source.PowerDbm,
                ReadIntervalMs = source.ReadIntervalMs,
                TidStartAddress = source.TidStartAddress,
                TidLength = source.TidLength,
                Options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            };

            if (source.Options != null)
            {
                foreach (var pair in source.Options) clone.Options[pair.Key] = pair.Value;
            }

            return clone;
        }

        private LaneCalibrationSessionConfig CurrentLaneCalibration()
        {
            lock (_gate) return _laneCalibration;
        }

        private static string LaneCalibrationReaderSignature(LaneCalibrationSessionConfig config)
        {
            if (config == null) return string.Empty;
            return string.Join(
                ";",
                (config.Readers ?? new List<LaneCalibrationReaderConfig>())
                    .Where(reader => reader != null)
                    .OrderBy(reader => reader.SerialNumber, StringComparer.OrdinalIgnoreCase)
                    .Select(reader =>
                        NormalizeSerial(reader.SerialNumber)
                        + "|P" + reader.PowerDbm
                        + "|I" + reader.ReadIntervalMs));
        }

        private static int MaximumPower(string driverKey)
        {
            return string.Equals(driverKey, "cf-e718", StringComparison.OrdinalIgnoreCase) ? 33 : 40;
        }

        private static int NormalizePower(string driverKey, int value)
        {
            return Math.Max(0, Math.Min(MaximumPower(driverKey), value));
        }

        private static string RestartReason(ReaderDeviceConfig current, ReaderDeviceConfig next)
        {
            if (current == null) return "current_configuration_missing";
            if (next == null) return "removed_from_configuration";
            if (!next.Enabled) return "disabled_by_server";
            if (!string.Equals(current.ConfigHash, next.ConfigHash, StringComparison.Ordinal)) return "runtime_configuration_changed";
            if (!string.Equals(current.DriverKey, next.DriverKey, StringComparison.OrdinalIgnoreCase)) return "driver_changed";
            if (!string.Equals(current.Endpoint, next.Endpoint, StringComparison.OrdinalIgnoreCase)) return "endpoint_changed";
            if (current.Port != next.Port) return "tcp_port_changed";
            return null;
        }

        private static string DescribeConfig(ReaderDeviceConfig config)
        {
            if (config == null) return "config=<null>";
            return "serial=" + (config.SerialNumber ?? "<empty>")
                + "; driver=" + (config.DriverKey ?? "<empty>")
                + "; enabled=" + config.Enabled
                + "; endpoint=" + (string.IsNullOrWhiteSpace(config.Endpoint) ? "AUTO-COM" : config.Endpoint)
                + "; tcp_port=" + config.Port
                + "; power_dbm=" + config.PowerDbm
                + "; read_interval_ms=" + config.ReadIntervalMs
                + "; tid_start=" + config.TidStartAddress
                + "; tid_length=" + config.TidLength
                + "; port_filtering=server"
                + "; config_hash=" + (config.ConfigHash ?? "<empty>");
        }

        private bool ClearLaneCalibrationLocked(string reason)
        {
            if (_laneCalibration == null) return false;
            var code = _laneCalibration.LaneCalibrationCode;
            _laneCalibration = null;
            _laneCalibrationReaders.Clear();

            if (_logger != null)
                _logger.Info(
                    "lane-calibration",
                    "Lane Calibration cleared; Parking mode restored",
                    "code=" + code + "; reason=" + (reason ?? "stopped"));
            return true;
        }

        private void StopRuntimeHandle(string serial, RuntimeHandle handle)
        {
            if (handle == null) return;
            handle.Runtime.DetectionReceived -= OnDetection;
            handle.Runtime.StatusChanged -= OnStatus;

            try { handle.Runtime.Dispose(); }
            catch (Exception ex)
            {
                if (_logger != null)
                    _logger.Error("reader-manager", "Reader runtime stop returned an error", ex, "serial=" + serial);
            }

            PersistStoppedStatus(handle.Config, "stopped");
            if (_logger != null)
                _logger.Info("reader-manager", "Reader runtime stopped", "serial=" + serial);
        }

        private void PersistStoppedStatus(ReaderDeviceConfig config, string message)
        {
            if (config == null) return;
            OnStatus(new ReaderStatus
            {
                DriverKey = config.DriverKey,
                SerialNumber = NormalizeSerial(config.SerialNumber),
                Model = config.DriverKey,
                Endpoint = config.Endpoint,
                Online = false,
                Message = string.IsNullOrWhiteSpace(message) ? "stopped" : message,
                PowerDbm = config.PowerDbm,
                ReadIntervalMs = config.ReadIntervalMs,
                Ports = ReaderPorts(config.DriverKey),
                UpdatedAtUtc = DateTime.UtcNow
            });
        }

        private static IList<int> ReaderPorts(string driverKey)
        {
            return string.Equals(driverKey, "cf-e718", StringComparison.OrdinalIgnoreCase)
                ? new List<int> { 1, 2, 3, 4 }
                : new List<int>();
        }

        private static bool IsComEndpoint(string endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint)) return false;
            var value = endpoint.Trim();
            if (!value.StartsWith("COM", StringComparison.OrdinalIgnoreCase)) return false;
            int port;
            return int.TryParse(value.Substring(3), out port) && port > 0;
        }

        private string BuildEventUid(string prefix)
        {
            var controller = string.IsNullOrWhiteSpace(_settings.ControllerCode)
                ? "CTRL"
                : _settings.ControllerCode.Trim().ToUpperInvariant();
            return controller + "-" + prefix + "-" + Guid.NewGuid().ToString("N");
        }

        private static string NormalizeSerial(string value)
        {
            return (value ?? string.Empty).Trim().ToUpperInvariant();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(GetType().FullName);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            lock (_applyGate)
            {
                List<KeyValuePair<string, RuntimeHandle>> stopped;
                lock (_gate)
                {
                    stopped = _runtimes.ToList();
                    _runtimes.Clear();
                    ClearLaneCalibrationLocked("controller_stopped");
                }

                foreach (var item in stopped)
                    StopRuntimeHandle(item.Key, item.Value);
            }
        }

        private sealed class RuntimeStopRequest
        {
            public RuntimeStopRequest(string serialNumber, RuntimeHandle handle, string reason)
            {
                SerialNumber = serialNumber;
                Handle = handle;
                Reason = reason;
            }

            public string SerialNumber { get; private set; }
            public RuntimeHandle Handle { get; private set; }
            public string Reason { get; private set; }
        }

        private sealed class RuntimeHandle
        {
            public RuntimeHandle(ReaderDeviceConfig config, IReaderRuntime runtime)
            {
                Config = config;
                Runtime = runtime;
            }

            public ReaderDeviceConfig Config { get; private set; }
            public IReaderRuntime Runtime { get; private set; }
        }
    }
}
