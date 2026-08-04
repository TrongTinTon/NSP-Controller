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
        private readonly Dictionary<string, RuntimeHandle> _runtimes = new Dictionary<string, RuntimeHandle>(StringComparer.OrdinalIgnoreCase);
        private readonly Queue<RfidDetection> _recentDetections = new Queue<RfidDetection>();
        private LaneCalibrationSessionConfig _laneCalibration;
        private HashSet<string> _laneCalibrationReaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> _laneCalibrationReaderPorts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool _disposed;

        public ReaderManager(ReaderDriverRegistry registry, LocalStore store, DetectionOutboxWriter outbox, FileLogger logger, AppSettings settings)
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
                lock (_gate) return _laneCalibration == null ? string.Empty : (_laneCalibration.LaneCalibrationCode ?? string.Empty);
            }
        }

        public string CurrentMode
        {
            get
            {
                lock (_gate) return _laneCalibration != null && _laneCalibration.IsRunningDesired ? "Lane Calibration" : "Parking";
            }
        }


        public void StartCachedConfiguration()
        {
            ApplyRuntimeConfiguration(_store.GetReaderConfigs());
        }

        public void ReloadCachedConfiguration()
        {
            ApplyRuntimeConfiguration(_store.GetReaderConfigs());
        }

        public void ApplyServerConfiguration(IList<ReaderDeviceConfig> serverConfigs)
        {
            serverConfigs = serverConfigs ?? new List<ReaderDeviceConfig>();
            var cached = _store.GetReaderConfigs()
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.SerialNumber))
                .ToDictionary(x => x.SerialNumber.Trim().ToUpperInvariant(), StringComparer.OrdinalIgnoreCase);

            var merged = new List<ReaderDeviceConfig>();
            foreach (var incoming in serverConfigs)
            {
                if (incoming == null || string.IsNullOrWhiteSpace(incoming.SerialNumber)) continue;
                var serial = incoming.SerialNumber.Trim().ToUpperInvariant();
                ReaderDeviceConfig local;
                cached.TryGetValue(serial, out local);
                merged.Add(MergePhysicalProfile(incoming, local));
            }

            foreach (var config in merged) _store.UpsertReaderConfig(config);
            _store.DisableReadersNotIn(merged.Select(x => x.SerialNumber).ToList());

            LaneCalibrationSessionConfig laneCalibration;
            lock (_gate) laneCalibration = _laneCalibration;
            ApplyRuntimeConfiguration(BuildEffectiveReaderConfigs(merged, laneCalibration));
        }

        public void ApplyLaneCalibrationConfiguration(LaneCalibrationSessionConfig config)
        {
            if (config == null || !config.IsRunningDesired || string.IsNullOrWhiteSpace(config.LaneCalibrationCode))
            {
                ClearLaneCalibration("server_stopped");
                return;
            }

            config.LaneCalibrationCode = (config.LaneCalibrationCode ?? string.Empty).Trim().ToUpperInvariant();
            if (config.Revision <= 0) config.Revision = 1;
            config.Readers = (config.Readers ?? new List<LaneCalibrationReaderConfig>())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.SerialNumber))
                .Select(x => new LaneCalibrationReaderConfig
                {
                    SerialNumber = x.SerialNumber.Trim().ToUpperInvariant(),
                    PowerDbm = Math.Max(0, Math.Min(40, x.PowerDbm)),
                    ReadIntervalMs = Math.Max(1, Math.Min(60000, x.ReadIntervalMs)),
                    Ports = (x.Ports ?? new List<int>())
                        .Where(number => number >= 1 && number <= 16)
                        .Distinct()
                        .OrderBy(number => number)
                        .ToList()
                })
                .Where(x => x.Ports.Count > 0)
                .GroupBy(x => x.SerialNumber, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(x => x.SerialNumber, StringComparer.OrdinalIgnoreCase)
                .ToList();

            ValidateLaneCalibrationReaders(config);

            bool changed;
            lock (_gate)
            {
                var newPairs = new HashSet<string>(
                    config.Readers.SelectMany(reader =>
                        reader.Ports.Select(portNo => ReaderPortKey(reader.SerialNumber, portNo))),
                    StringComparer.OrdinalIgnoreCase);
                var newReaders = new HashSet<string>(
                    config.Readers.Select(reader => reader.SerialNumber),
                    StringComparer.OrdinalIgnoreCase);
                changed = _laneCalibration == null
                          || !string.Equals(_laneCalibration.LaneCalibrationCode, config.LaneCalibrationCode, StringComparison.OrdinalIgnoreCase)
                          || _laneCalibration.Revision != config.Revision
                          || !string.Equals(LaneCalibrationReaderSignature(_laneCalibration), LaneCalibrationReaderSignature(config), StringComparison.Ordinal);

                _laneCalibration = config;
                _laneCalibrationReaders = newReaders;
                _laneCalibrationReaderPorts = newPairs;
            }

            if (changed)
            {
                ApplyRuntimeConfiguration(BuildEffectiveReaderConfigs(_store.GetReaderConfigs(), config));
                if (_logger != null)
                    _logger.Info(
                        "lane-calibration",
                        "Lane Calibration runtime applied",
                        "code=" + config.LaneCalibrationCode
                        + "; revision=" + config.Revision
                        + "; readers=" + config.Readers.Count
                        + "; ports=" + _laneCalibrationReaderPorts.Count);
            }
        }

        private void ValidateLaneCalibrationReaders(LaneCalibrationSessionConfig config)
        {
            if (config.Readers.Count == 0)
                throw new InvalidOperationException("Lane Calibration has no Reader Port configuration.");

            var operational = _store.GetReaderConfigs()
                .Where(reader => reader != null && reader.Enabled && !string.IsNullOrWhiteSpace(reader.SerialNumber))
                .ToDictionary(reader => reader.SerialNumber.Trim().ToUpperInvariant(), StringComparer.OrdinalIgnoreCase);

            foreach (var calibrationReader in config.Readers)
            {
                ReaderDeviceConfig reader;
                if (!operational.TryGetValue(calibrationReader.SerialNumber, out reader))
                    throw new InvalidOperationException("Lane Calibration Reader is not configured on this Controller: " + calibrationReader.SerialNumber);

                var invalidPorts = calibrationReader.Ports.Except(reader.PortNumbers()).OrderBy(value => value).ToList();
                if (invalidPorts.Count > 0)
                    throw new InvalidOperationException(
                        "Lane Calibration contains unconfigured Reader Port(s): "
                        + calibrationReader.SerialNumber + "/" + string.Join(",", invalidPorts));

                var maxPower = MaximumPower(reader.DriverKey);
                if (calibrationReader.PowerDbm > maxPower)
                    throw new InvalidOperationException(
                        "Lane Calibration power exceeds Reader driver capability: "
                        + calibrationReader.SerialNumber + "; requested=" + calibrationReader.PowerDbm + "; maximum=" + maxPower);
            }
        }

        private static string LaneCalibrationReaderSignature(LaneCalibrationSessionConfig config)
        {
            if (config == null) return string.Empty;
            return string.Join(";",
                (config.Readers ?? new List<LaneCalibrationReaderConfig>())
                    .Where(reader => reader != null)
                    .OrderBy(reader => reader.SerialNumber, StringComparer.OrdinalIgnoreCase)
                    .Select(reader =>
                        (reader.SerialNumber ?? string.Empty).Trim().ToUpperInvariant()
                        + "|P" + reader.PowerDbm
                        + "|I" + reader.ReadIntervalMs
                        + "|PORTS=" + string.Join(",", (reader.Ports ?? new List<int>()).OrderBy(number => number))));
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

        private ReaderDeviceConfig MergePhysicalProfile(ReaderDeviceConfig incoming, ReaderDeviceConfig local)
        {
            incoming.SerialNumber = (incoming.SerialNumber ?? string.Empty).Trim().ToUpperInvariant();
            if (local != null)
            {
                if (string.IsNullOrWhiteSpace(incoming.DriverKey)) incoming.DriverKey = local.DriverKey;
                if (string.IsNullOrWhiteSpace(incoming.Endpoint)) incoming.Endpoint = local.Endpoint;
                if (incoming.Port <= 0) incoming.Port = local.Port;

                if (local.Options != null)
                {
                    foreach (var pair in local.Options)
                        if (!incoming.Options.ContainsKey(pair.Key)) incoming.Options[pair.Key] = pair.Value;
                }
            }

            if (string.IsNullOrWhiteSpace(incoming.DriverKey)) incoming.DriverKey = "cf-e718";
            incoming.DriverKey = incoming.DriverKey.Trim().ToLowerInvariant();
            incoming.PowerDbm = NormalizePower(incoming.DriverKey, incoming.PowerDbm);
            if (string.IsNullOrWhiteSpace(incoming.Endpoint) &&
                string.Equals(incoming.DriverKey, "cf-e718", StringComparison.OrdinalIgnoreCase) &&
                !incoming.Options.ContainsKey("connection"))
            {
                incoming.Options["connection"] = "com";
            }
            incoming.Enabled = incoming.Enabled && incoming.PortNumbers().Count > 0;
            if (!incoming.Enabled && _logger != null)
                _logger.Warn("reader-config", "Reader has no runtime ports and will remain stopped", "serial=" + incoming.SerialNumber);
            return incoming;
        }

        private void ApplyRuntimeConfiguration(IList<ReaderDeviceConfig> configs)
        {
            ThrowIfDisposed();
            configs = configs ?? new List<ReaderDeviceConfig>();
            var desired = configs
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.SerialNumber))
                .ToDictionary(x => x.SerialNumber.Trim().ToUpperInvariant(), StringComparer.OrdinalIgnoreCase);

            lock (_gate)
            {
                var toStop = _runtimes.Keys
                    .Where(serial => !desired.ContainsKey(serial) || !desired[serial].Enabled || NeedsRestart(_runtimes[serial].Config, desired[serial]))
                    .ToList();
                foreach (var serial in toStop) StopRuntime(serial);

                foreach (var config in desired.Values.Where(x => x.Enabled))
                {
                    var serial = config.SerialNumber.Trim().ToUpperInvariant();
                    if (_runtimes.ContainsKey(serial)) continue;
                    try
                    {
                        var runtime = _registry.Create(config);
                        runtime.DetectionReceived += OnDetection;
                        runtime.StatusChanged += OnStatus;
                        _runtimes[serial] = new RuntimeHandle(config, runtime);
                        runtime.Start();
                        if (_logger != null) _logger.Info("reader-manager", "Reader runtime started", "serial=" + serial + "; driver=" + config.DriverKey);
                    }
                    catch (Exception ex)
                    {
                        if (_logger != null) _logger.Error("reader-manager", "Reader runtime start failed: " + serial, ex);
                        OnStatus(new ReaderStatus
                        {
                            DriverKey = config.DriverKey,
                            SerialNumber = serial,
                            Model = config.DriverKey,
                            Endpoint = config.Endpoint,
                            Online = false,
                            Message = ex.Message,
                            PowerDbm = config.PowerDbm,
                            ReadIntervalMs = config.ReadIntervalMs,
                            Ports = config.PortNumbers(),
                            UpdatedAtUtc = DateTime.UtcNow
                        });
                    }
                }
            }
        }

        private void OnDetection(RfidDetection detection)
        {
            if (detection == null || string.IsNullOrWhiteSpace(detection.SerialNumber) || detection.PortNo <= 0 || string.IsNullOrWhiteSpace(detection.Tid)) return;
            detection.SerialNumber = detection.SerialNumber.Trim().ToUpperInvariant();
            detection.Tid = detection.Tid.Trim().ToUpperInvariant();
            detection.EventUid = BuildEventUid("RFID");

            bool laneCalibrationReader;
            bool laneCalibrationReaderPort;
            LaneCalibrationSessionConfig laneCalibration;
            ReaderDeviceConfig appliedConfig = null;
            lock (_gate)
            {
                laneCalibration = _laneCalibration;
                laneCalibrationReader = laneCalibration != null && laneCalibration.IsRunningDesired && _laneCalibrationReaders.Contains(detection.SerialNumber);
                laneCalibrationReaderPort = laneCalibrationReader && _laneCalibrationReaderPorts.Contains(ReaderPortKey(detection.SerialNumber, detection.PortNo));
                RuntimeHandle handle;
                if (_runtimes.TryGetValue(detection.SerialNumber, out handle)) appliedConfig = handle.Config;

                _recentDetections.Enqueue(detection);
                while (_recentDetections.Count > 500) _recentDetections.Dequeue();
            }

            try
            {
                if (laneCalibrationReader)
                {
                    if (laneCalibrationReaderPort)
                    {
                        var requestedConfig = laneCalibration.Reader(detection.SerialNumber);
                        _outbox.EnqueueLaneCalibration(new LaneCalibrationEvent
                        {
                            EventUid = BuildEventUid("CAL"),
                            LaneCalibrationCode = laneCalibration.LaneCalibrationCode,
                            Revision = laneCalibration.Revision,
                            PowerDbm = appliedConfig == null ? (requestedConfig == null ? 0 : requestedConfig.PowerDbm) : appliedConfig.PowerDbm,
                            ReadIntervalMs = appliedConfig == null ? (requestedConfig == null ? 200 : requestedConfig.ReadIntervalMs) : appliedConfig.ReadIntervalMs,
                            SerialNumber = detection.SerialNumber,
                            PortNo = detection.PortNo,
                            Tid = detection.Tid,
                            RssiDbm = detection.RssiDbm,
                            ReadAtUtc = detection.DetectedAtUtc
                        });
                    }
                }
                else
                {
                    _outbox.EnqueueParking(detection);
                }
            }
            catch (Exception ex)
            {
                if (_logger != null) _logger.Error("rfid-outbox", "Could not queue RFID event", ex);
                return;
            }

        }

        private void OnStatus(ReaderStatus status)
        {
            if (status == null) return;
            lock (_gate)
            {
                RuntimeHandle handle;
                var serial = (status.SerialNumber ?? string.Empty).Trim().ToUpperInvariant();
                if (_runtimes.TryGetValue(serial, out handle))
                {
                    status.SerialNumber = serial;
                    status.PowerDbm = handle.Config.PowerDbm;
                    status.ReadIntervalMs = handle.Config.ReadIntervalMs;
                    status.Ports = handle.Config.PortNumbers();
                }
            }
            try { _store.UpsertReaderStatus(status); }
            catch (Exception ex) { if (_logger != null) _logger.Error("reader-status", "Could not persist reader status", ex); }
        }

        private string BuildEventUid(string prefix)
        {
            var controller = string.IsNullOrWhiteSpace(_settings.ControllerCode) ? "CTRL" : _settings.ControllerCode.Trim().ToUpperInvariant();
            return controller + "-" + prefix + "-" + Guid.NewGuid().ToString("N");
        }

        private IList<ReaderDeviceConfig> BuildEffectiveReaderConfigs(IList<ReaderDeviceConfig> baseConfigs, LaneCalibrationSessionConfig laneCalibration)
        {
            baseConfigs = baseConfigs ?? new List<ReaderDeviceConfig>();
            if (laneCalibration == null || !laneCalibration.IsRunningDesired) return baseConfigs;

            var selectedReaders = (laneCalibration.Readers ?? new List<LaneCalibrationReaderConfig>())
                .Where(reader => reader != null && !string.IsNullOrWhiteSpace(reader.SerialNumber))
                .ToDictionary(
                    reader => reader.SerialNumber.Trim().ToUpperInvariant(),
                    reader => reader,
                    StringComparer.OrdinalIgnoreCase);

            return baseConfigs.Select(config =>
            {
                if (config == null) return config;
                var serial = (config.SerialNumber ?? string.Empty).Trim().ToUpperInvariant();
                LaneCalibrationReaderConfig calibrationReader;
                if (!selectedReaders.TryGetValue(serial, out calibrationReader)) return config;

                var selectedPorts = new HashSet<int>(
                    calibrationReader.Ports ?? new List<int>());
                var clone = CloneReaderConfig(config);
                clone.PowerDbm = NormalizePower(clone.DriverKey, calibrationReader.PowerDbm);
                clone.ReadIntervalMs = Math.Max(1, Math.Min(60000, calibrationReader.ReadIntervalMs));
                clone.Ports = (clone.Ports ?? new List<int>())
                    .Where(selectedPorts.Contains)
                    .Distinct()
                    .OrderBy(value => value)
                    .ToList();
                clone.ConfigHash = (config.ConfigHash ?? string.Empty)
                                   + "|CAL|" + laneCalibration.LaneCalibrationCode
                                   + "|R" + laneCalibration.Revision
                                   + "|P" + clone.PowerDbm
                                   + "|I" + clone.ReadIntervalMs
                                   + "|PORTS=" + string.Join(",", clone.PortNumbers());
                return clone;
            }).Where(x => x != null).ToList();
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
                Ports = (source.Ports ?? new List<int>())
                    .Where(value => value >= 1 && value <= 16)
                    .Distinct()
                    .OrderBy(value => value)
                    .ToList(),
                Options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            };
            if (source.Options != null)
            {
                foreach (var pair in source.Options) clone.Options[pair.Key] = pair.Value;
            }
            return clone;
        }

        private static string ReaderPortKey(string serial, int portNo)
        {
            return (serial ?? string.Empty).Trim().ToUpperInvariant() + "|" + portNo;
        }

        private static int MaximumPower(string driverKey)
        {
            return string.Equals(driverKey, "cf-e718", StringComparison.OrdinalIgnoreCase) ? 33 : 40;
        }

        private static int NormalizePower(string driverKey, int value)
        {
            return Math.Max(0, Math.Min(MaximumPower(driverKey), value));
        }

        private static bool NeedsRestart(ReaderDeviceConfig current, ReaderDeviceConfig next)
        {
            if (current == null || next == null) return true;
            if (!string.Equals(current.ConfigHash, next.ConfigHash, StringComparison.Ordinal)) return true;
            if (!string.Equals(current.DriverKey, next.DriverKey, StringComparison.OrdinalIgnoreCase)) return true;
            if (!string.Equals(current.Endpoint, next.Endpoint, StringComparison.OrdinalIgnoreCase)) return true;
            if (current.Port != next.Port) return true;
            return false;
        }

        private bool ClearLaneCalibrationLocked(string reason)
        {
            if (_laneCalibration == null) return false;
            var code = _laneCalibration.LaneCalibrationCode;
            _laneCalibration = null;
            _laneCalibrationReaders.Clear();
            _laneCalibrationReaderPorts.Clear();
            if (_logger != null) _logger.Info("lane-calibration", "Lane Calibration cleared; Parking mode restored", "code=" + code + "; reason=" + (reason ?? "stopped"));
            return true;
        }

        private void StopRuntime(string serial)
        {
            RuntimeHandle handle;
            if (!_runtimes.TryGetValue(serial, out handle)) return;
            _runtimes.Remove(serial);
            try
            {
                handle.Runtime.DetectionReceived -= OnDetection;
                handle.Runtime.StatusChanged -= OnStatus;
                handle.Runtime.Dispose();
            }
            catch { }
            OnStatus(new ReaderStatus
            {
                DriverKey = handle.Config.DriverKey,
                SerialNumber = serial,
                Model = handle.Config.DriverKey,
                Endpoint = handle.Config.Endpoint,
                Online = false,
                Message = "stopped",
                PowerDbm = handle.Config.PowerDbm,
                ReadIntervalMs = handle.Config.ReadIntervalMs,
                Ports = handle.Config.PortNumbers(),
                UpdatedAtUtc = DateTime.UtcNow
            });
            if (_logger != null) _logger.Info("reader-manager", "Reader runtime stopped", "serial=" + serial);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(GetType().FullName);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            lock (_gate)
            {
                foreach (var serial in _runtimes.Keys.ToList()) StopRuntime(serial);
                ClearLaneCalibrationLocked("controller_stopped");
            }
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
