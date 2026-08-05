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
        private readonly Dictionary<string, RuntimeHandle> _runtimes =
            new Dictionary<string, RuntimeHandle>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ReaderDeviceConfig> _serverConfigs =
            new Dictionary<string, ReaderDeviceConfig>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _lastStatusSignatures =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Queue<RfidDetection> _recentDetections = new Queue<RfidDetection>();
        private LaneCalibrationSessionConfig _laneCalibration;
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
                    return _laneCalibration == null ? string.Empty : (_laneCalibration.LaneCalibrationCode ?? string.Empty);
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
            ReplaceServerConfigurations(_store.GetReaderConfigs(), false);
            DiscoverReadersOnce();
        }

        public void ApplyServerConfiguration(IList<ReaderDeviceConfig> serverConfigs)
        {
            ReplaceServerConfigurations(serverConfigs, true);
            ReapplyRuntimeSettings();
            DiscoverReadersOnce();
        }

        public void DiscoverReadersOnce()
        {
            ThrowIfDisposed();
            HashSet<string> excludedEndpoints;
            lock (_gate)
            {
                excludedEndpoints = new HashSet<string>(
                    _runtimes.Values.Select(value => value.Config.Endpoint)
                        .Where(value => !string.IsNullOrWhiteSpace(value)),
                    StringComparer.OrdinalIgnoreCase);
            }

            var observations = _registry.Discover(excludedEndpoints)
                .Where(item => item != null && !string.IsNullOrWhiteSpace(item.SerialNumber))
                .GroupBy(item => NormalizeSerial(item.SerialNumber), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(item => item.DiscoveredAtUtc).First())
                .ToList();

            foreach (var observation in observations)
                StartOrRebindObservation(observation);
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
                    ReadIntervalMs = Math.Max(1, Math.Min(60000, reader.ReadIntervalMs)),
                })
                .GroupBy(reader => reader.SerialNumber, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Last())
                .ToList();

            bool changed;
            lock (_gate)
            {
                changed = LaneCalibrationSignature(_laneCalibration) != LaneCalibrationSignature(config);
                _laneCalibration = config;
            }
            if (changed) ReapplyRuntimeSettings();

            if (_logger != null)
                _logger.Info(
                    "lane-calibration",
                    "Lane Calibration acquisition mode applied",
                    "code=" + config.LaneCalibrationCode + "; revision=" + config.Revision
                    + "; configured_reader_parameters=" + config.Readers.Count
                    + "; observation_filtering=edge");
        }

        public void ClearLaneCalibration(string reason)
        {
            bool changed;
            string code;
            lock (_gate)
            {
                changed = _laneCalibration != null;
                code = _laneCalibration == null ? string.Empty : _laneCalibration.LaneCalibrationCode;
                _laneCalibration = null;
            }
            if (changed && !_disposed)
            {
                ReapplyRuntimeSettings();
                if (_logger != null)
                    _logger.Info("lane-calibration", "Lane Calibration acquisition mode cleared",
                        "code=" + code + "; reason=" + (reason ?? "stopped"));
            }
        }

        public IList<ReaderStatus> GetStatuses()
        {
            return _store.GetReaderStatuses();
        }

        public IList<RfidDetection> GetRecentDetections()
        {
            lock (_gate) return _recentDetections.ToList();
        }

        private void ReplaceServerConfigurations(IList<ReaderDeviceConfig> configs, bool persist)
        {
            configs = configs ?? new List<ReaderDeviceConfig>();
            var prepared = configs
                .Where(config => config != null && !string.IsNullOrWhiteSpace(config.SerialNumber))
                .Select(CloneReaderConfig)
                .GroupBy(config => NormalizeSerial(config.SerialNumber), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Last())
                .ToList();

            foreach (var config in prepared)
            {
                config.SerialNumber = NormalizeSerial(config.SerialNumber);
                config.DriverKey = string.IsNullOrWhiteSpace(config.DriverKey) ? "cf-e718" : config.DriverKey.Trim().ToLowerInvariant();
                config.PowerDbm = NormalizePower(config.DriverKey, config.PowerDbm);
                config.ReadIntervalMs = Math.Max(1, Math.Min(60000, config.ReadIntervalMs));
                config.TidStartAddress = Math.Max(0, config.TidStartAddress);
                config.TidLength = Math.Max(1, config.TidLength);
                config.Options = config.Options ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (persist) _store.UpsertReaderConfig(config);
            }
            if (persist) _store.DisableReadersNotIn(prepared.Select(config => config.SerialNumber).ToList());

            lock (_gate)
            {
                _serverConfigs.Clear();
                foreach (var config in prepared) _serverConfigs[config.SerialNumber] = config;
            }

            if (_logger != null)
                _logger.Info(
                    "reader-config",
                    "Runtime Reader parameters cached",
                    "count=" + prepared.Count + "; lifecycle_owner=physical_discovery");
        }

        private void StartOrRebindObservation(ReaderDiscoveryObservation observation)
        {
            var serial = NormalizeSerial(observation.SerialNumber);
            var endpoint = NormalizeEndpoint(observation.Endpoint);
            if (string.IsNullOrWhiteSpace(serial) || string.IsNullOrWhiteSpace(endpoint)) return;

            RuntimeHandle existing = null;
            string existingKey = null;
            lock (_gate)
            {
                if (_runtimes.TryGetValue(serial, out existing)) existingKey = serial;
                if (existing == null)
                {
                    var pair = _runtimes.FirstOrDefault(value =>
                        string.Equals(value.Value.Config.Endpoint, endpoint, StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrWhiteSpace(pair.Key))
                    {
                        existingKey = pair.Key;
                        existing = pair.Value;
                    }
                }
            }

            if (existing != null
                && string.Equals(existing.Config.SerialNumber, serial, StringComparison.OrdinalIgnoreCase)
                && string.Equals(existing.Config.Endpoint, endpoint, StringComparison.OrdinalIgnoreCase))
                return;

            if (existing != null)
            {
                lock (_gate) _runtimes.Remove(existingKey);
                StopRuntimeHandle(existingKey, existing, "physical_observation_changed");
            }

            var config = EffectiveConfig(serial, observation.DriverKey, endpoint);
            StartRuntime(config, observation.FirmwareVersion);
        }

        private void ReapplyRuntimeSettings()
        {
            List<RuntimeStopRequest> restart;
            lock (_gate)
            {
                restart = _runtimes.Select(pair =>
                {
                    var next = EffectiveConfigLocked(pair.Key, pair.Value.Config.DriverKey, pair.Value.Config.Endpoint);
                    return RestartReason(pair.Value.Config, next) == null
                        ? null
                        : new RuntimeStopRequest(pair.Key, pair.Value, next);
                }).Where(value => value != null).ToList();
                foreach (var item in restart) _runtimes.Remove(item.SerialNumber);
            }

            foreach (var item in restart)
            {
                StopRuntimeHandle(item.SerialNumber, item.Handle, "runtime_parameters_changed");
                StartRuntime(item.NextConfig, null);
            }
        }

        private void StartRuntime(ReaderDeviceConfig config, string observedFirmware)
        {
            IReaderRuntime runtime = null;
            var serial = NormalizeSerial(config.SerialNumber);
            try
            {
                runtime = _registry.Create(config);
                runtime.DetectionReceived += OnDetection;
                runtime.StatusChanged += OnStatus;
                lock (_gate)
                {
                    if (_runtimes.ContainsKey(serial))
                    {
                        runtime.Dispose();
                        return;
                    }
                    _runtimes[serial] = new RuntimeHandle(config, runtime);
                }
                runtime.Start();

                OnStatus(new ReaderStatus
                {
                    DriverKey = config.DriverKey,
                    SerialNumber = serial,
                    DetectedSdkSerialNumber = serial,
                    DetectedEndpoint = config.Endpoint,
                    Endpoint = config.Endpoint,
                    Model = "CF-E718",
                    Online = false,
                    Message = "discovered",
                    FirmwareVersion = observedFirmware,
                    PowerDbm = config.PowerDbm,
                    ReadIntervalMs = config.ReadIntervalMs,
                    Ports = ReaderPorts(config.DriverKey),
                    UpdatedAtUtc = DateTime.UtcNow,
                });
            }
            catch (Exception ex)
            {
                lock (_gate) _runtimes.Remove(serial);
                if (runtime != null)
                {
                    runtime.DetectionReceived -= OnDetection;
                    runtime.StatusChanged -= OnStatus;
                    try { runtime.Dispose(); } catch { }
                }
                if (_logger != null)
                    _logger.Error("reader-runtime", "Physical Reader worker could not start", ex,
                        "serial=" + serial + "; endpoint=" + config.Endpoint);
            }
        }

        private ReaderDeviceConfig EffectiveConfig(string serial, string driverKey, string endpoint)
        {
            lock (_gate) return EffectiveConfigLocked(serial, driverKey, endpoint);
        }

        private ReaderDeviceConfig EffectiveConfigLocked(string serial, string driverKey, string endpoint)
        {
            serial = NormalizeSerial(serial);
            ReaderDeviceConfig server;
            _serverConfigs.TryGetValue(serial, out server);
            var config = server == null ? new ReaderDeviceConfig() : CloneReaderConfig(server);
            config.DriverKey = string.IsNullOrWhiteSpace(driverKey)
                ? (string.IsNullOrWhiteSpace(config.DriverKey) ? "cf-e718" : config.DriverKey)
                : driverKey;
            config.SerialNumber = serial;
            config.Endpoint = NormalizeEndpoint(endpoint);
            config.Port = 0;
            config.Enabled = true; // Discovery owns lifecycle; Server does not permit/deny physical observation.
            config.Options = config.Options ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            config.Options["connection"] = "com";

            LaneCalibrationReaderConfig calibrationReader = null;
            if (_laneCalibration != null && _laneCalibration.IsRunningDesired)
                calibrationReader = _laneCalibration.Reader(serial);
            if (calibrationReader != null)
            {
                config.PowerDbm = NormalizePower(config.DriverKey, calibrationReader.PowerDbm);
                config.ReadIntervalMs = Math.Max(1, Math.Min(60000, calibrationReader.ReadIntervalMs));
            }
            else
            {
                config.PowerDbm = NormalizePower(config.DriverKey, config.PowerDbm);
                config.ReadIntervalMs = Math.Max(1, Math.Min(60000, config.ReadIntervalMs));
            }
            config.ConfigHash = RuntimeSignature(config);
            return config;
        }

        private void OnDetection(RfidDetection detection)
        {
            if (detection == null || string.IsNullOrWhiteSpace(detection.SerialNumber)
                || detection.PortNo <= 0 || string.IsNullOrWhiteSpace(detection.Tid)) return;

            detection.SerialNumber = NormalizeSerial(detection.SerialNumber);
            detection.Tid = detection.Tid.Trim().ToUpperInvariant();
            detection.EventUid = BuildEventUid("RFID");

            LaneCalibrationSessionConfig calibration;
            ReaderDeviceConfig applied = null;
            lock (_gate)
            {
                calibration = _laneCalibration;
                RuntimeHandle handle;
                if (_runtimes.TryGetValue(detection.SerialNumber, out handle)) applied = handle.Config;
                _recentDetections.Enqueue(detection);
                while (_recentDetections.Count > 500) _recentDetections.Dequeue();
            }

            if (calibration == null || !calibration.IsRunningDesired)
            {
                _outbox.EnqueueParking(detection);
                return;
            }

            _outbox.EnqueueLaneCalibration(new LaneCalibrationEvent
            {
                EventUid = BuildEventUid("CAL"),
                LaneCalibrationCode = calibration.LaneCalibrationCode,
                Revision = calibration.Revision,
                PowerDbm = applied == null ? 0 : applied.PowerDbm,
                ReadIntervalMs = applied == null ? 200 : applied.ReadIntervalMs,
                SerialNumber = detection.SerialNumber,
                PortNo = detection.PortNo,
                Tid = detection.Tid,
                RssiDbm = detection.RssiDbm,
                ReadAtUtc = detection.DetectedAtUtc,
            });
        }

        private void OnStatus(ReaderStatus status)
        {
            if (status == null) return;
            var serial = NormalizeSerial(status.SerialNumber ?? status.DetectedSdkSerialNumber);
            if (string.IsNullOrWhiteSpace(serial)) return;
            status.SerialNumber = serial;
            status.DetectedSdkSerialNumber = serial;
            status.DetectedEndpoint = NormalizeEndpoint(status.DetectedEndpoint ?? status.Endpoint);
            status.Endpoint = status.DetectedEndpoint;
            status.Ports = (status.Ports ?? new List<int>()).Where(value => value >= 1 && value <= 16)
                .Distinct().OrderBy(value => value).ToList();

            lock (_gate)
            {
                RuntimeHandle handle;
                if (!_runtimes.TryGetValue(serial, out handle) && !string.IsNullOrWhiteSpace(status.Endpoint))
                {
                    var pair = _runtimes.FirstOrDefault(value =>
                        string.Equals(value.Value.Config.Endpoint, status.Endpoint, StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrWhiteSpace(pair.Key))
                    {
                        handle = pair.Value;
                        if (!string.Equals(pair.Key, serial, StringComparison.OrdinalIgnoreCase))
                        {
                            _runtimes.Remove(pair.Key);
                            handle.Config.SerialNumber = serial;
                            _runtimes[serial] = handle;
                        }
                    }
                }
                if (handle != null)
                {
                    status.PowerDbm = handle.Config.PowerDbm;
                    status.ReadIntervalMs = handle.Config.ReadIntervalMs;
                }

                var signature = status.Online + "|" + (status.Endpoint ?? "") + "|" + (status.Message ?? "");
                string previous;
                if (!_lastStatusSignatures.TryGetValue(serial, out previous) || previous != signature)
                {
                    _lastStatusSignatures[serial] = signature;
                    if (_logger != null)
                        _logger.Info("reader-status", "Physical Reader observation changed",
                            "serial=" + serial + "; endpoint=" + (status.Endpoint ?? "")
                            + "; online=" + status.Online + "; state=" + (status.Message ?? ""));
                }
            }

            try { _store.UpsertReaderStatus(status); }
            catch (Exception ex)
            {
                if (_logger != null) _logger.Error("reader-status", "Could not cache physical Reader observation", ex,
                    "serial=" + serial);
            }
        }

        private void StopRuntimeHandle(string serial, RuntimeHandle handle, string reason)
        {
            if (handle == null) return;
            handle.Runtime.DetectionReceived -= OnDetection;
            handle.Runtime.StatusChanged -= OnStatus;
            try { handle.Runtime.Dispose(); }
            catch (Exception ex)
            {
                if (_logger != null) _logger.Error("reader-runtime", "Reader worker stop returned an error", ex,
                    "serial=" + serial);
            }
            if (_logger != null)
                _logger.Info("reader-runtime", "Physical Reader worker stopped",
                    "serial=" + serial + "; reason=" + reason);
        }

        private static ReaderDeviceConfig CloneReaderConfig(ReaderDeviceConfig source)
        {
            if (source == null) return new ReaderDeviceConfig();
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
                Options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            };
            if (source.Options != null)
                foreach (var pair in source.Options) clone.Options[pair.Key] = pair.Value;
            return clone;
        }

        private static string RuntimeSignature(ReaderDeviceConfig config)
        {
            return string.Join("|", new[]
            {
                NormalizeSerial(config.SerialNumber),
                (config.DriverKey ?? "").Trim().ToLowerInvariant(),
                NormalizeEndpoint(config.Endpoint),
                config.PowerDbm.ToString(),
                config.ReadIntervalMs.ToString(),
                config.TidStartAddress.ToString(),
                config.TidLength.ToString(),
            });
        }

        private static string RestartReason(ReaderDeviceConfig current, ReaderDeviceConfig next)
        {
            return current != null && next != null && RuntimeSignature(current) == RuntimeSignature(next)
                ? null
                : "runtime_configuration_changed";
        }

        private static string LaneCalibrationSignature(LaneCalibrationSessionConfig config)
        {
            if (config == null) return string.Empty;
            return (config.LaneCalibrationCode ?? "") + "|" + config.Revision + "|" + string.Join(";",
                (config.Readers ?? new List<LaneCalibrationReaderConfig>())
                    .OrderBy(value => value.SerialNumber)
                    .Select(value => value.SerialNumber + ":" + value.PowerDbm + ":" + value.ReadIntervalMs));
        }

        private static int MaximumPower(string driverKey)
        {
            return string.Equals(driverKey, "cf-e718", StringComparison.OrdinalIgnoreCase) ? 33 : 40;
        }

        private static int NormalizePower(string driverKey, int value)
        {
            return Math.Max(0, Math.Min(MaximumPower(driverKey), value));
        }

        private static IList<int> ReaderPorts(string driverKey)
        {
            return string.Equals(driverKey, "cf-e718", StringComparison.OrdinalIgnoreCase)
                ? new List<int> { 1, 2, 3, 4 }
                : new List<int>();
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

        private static string NormalizeEndpoint(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(GetType().FullName);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            List<KeyValuePair<string, RuntimeHandle>> handles;
            lock (_gate)
            {
                handles = _runtimes.ToList();
                _runtimes.Clear();
            }
            foreach (var pair in handles) StopRuntimeHandle(pair.Key, pair.Value, "controller_stopped");
        }

        private sealed class RuntimeStopRequest
        {
            public RuntimeStopRequest(string serialNumber, RuntimeHandle handle, ReaderDeviceConfig nextConfig)
            {
                SerialNumber = serialNumber;
                Handle = handle;
                NextConfig = nextConfig;
            }
            public string SerialNumber { get; private set; }
            public RuntimeHandle Handle { get; private set; }
            public ReaderDeviceConfig NextConfig { get; private set; }
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
