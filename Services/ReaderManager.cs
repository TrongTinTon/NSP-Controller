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
        private IList<ParkingLayoutRuntimeInfo> _parkingLayouts = new List<ParkingLayoutRuntimeInfo>();
        private DateTime _lastNoRuntimeDetectionLogUtc = DateTime.MinValue;
        private DateTime _lastLaneCalibrationRouteLogUtc = DateTime.MinValue;
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
            get { return GetRuntimeContextSnapshot().Mode; }
        }

        public bool IsParkingRuntimeActive
        {
            get
            {
                lock (_gate) return HasActiveParkingRuntime(_parkingLayouts);
            }
        }

        public bool IsLaneCalibrationRuntimeActive
        {
            get
            {
                lock (_gate) return _laneCalibration != null && _laneCalibration.IsActiveForController;
            }
        }

        public ControllerRuntimeContextSnapshot GetRuntimeContextSnapshot()
        {
            lock (_gate)
            {
                var calibration = CloneLaneCalibration(_laneCalibration);
                var layouts = (_parkingLayouts ?? new List<ParkingLayoutRuntimeInfo>())
                    .Select(CloneParkingLayout)
                    .OrderBy(value => value.Code, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return new ControllerRuntimeContextSnapshot
                {
                    Mode = calibration != null && calibration.IsActiveForController
                        ? "Lane Calibration"
                        : (HasActiveParkingRuntime(layouts) ? "Parking Layout" : "Idle"),
                    ParkingLayouts = layouts,
                    LaneCalibration = calibration,
                };
            }
        }

        public void StartCachedConfiguration()
        {
            ReplaceServerConfigurations(_store.GetReaderConfigs(), false);
            lock (_gate)
            {
                _parkingLayouts = (_store.GetParkingLayouts() ?? new List<ParkingLayoutRuntimeInfo>())
                    .Select(CloneParkingLayout)
                    .ToList();
            }
            DiscoverReadersOnce();
        }

        public void ApplyServerConfiguration(ControllerRuntimeConfigurationSnapshot snapshot)
        {
            snapshot = snapshot ?? new ControllerRuntimeConfigurationSnapshot();
            ReplaceServerConfigurations(snapshot.Devices, true);
            var parkingLayouts = (snapshot.ParkingLayouts ?? new List<ParkingLayoutRuntimeInfo>())
                .Select(CloneParkingLayout)
                .ToList();
            lock (_gate) _parkingLayouts = parkingLayouts;
            _store.SaveParkingLayouts(parkingLayouts);
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
            if (config == null || !config.IsActiveForController || string.IsNullOrWhiteSpace(config.LaneCalibrationCode))
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
                    TidStartAddress = Math.Max(0, reader.TidStartAddress),
                    TidLength = Math.Max(1, reader.TidLength),
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
            var inPlace = new List<RuntimeUpdateRequest>();
            var restart = new List<RuntimeStopRequest>();

            lock (_gate)
            {
                foreach (var pair in _runtimes.ToList())
                {
                    var next = EffectiveConfigLocked(pair.Key, pair.Value.Config.DriverKey, pair.Value.Config.Endpoint);
                    if (RestartReason(pair.Value.Config, next) == null) continue;

                    if (CanApplyInPlace(pair.Value.Config, next))
                        inPlace.Add(new RuntimeUpdateRequest(pair.Key, pair.Value, next));
                    else
                    {
                        restart.Add(new RuntimeStopRequest(pair.Key, pair.Value, next));
                        _runtimes.Remove(pair.Key);
                    }
                }
            }

            foreach (var item in inPlace)
            {
                bool applied = false;
                try { applied = item.Handle.Runtime.TryApplyConfiguration(item.NextConfig); }
                catch (Exception ex)
                {
                    if (_logger != null)
                        _logger.Error("reader-runtime", "In-place Reader parameter update failed", ex,
                            "serial=" + item.SerialNumber);
                }

                if (applied)
                {
                    if (_logger != null)
                        _logger.Info(
                            "reader-runtime",
                            "Reader parameters scheduled without restarting RFID acquisition",
                            "serial=" + item.SerialNumber
                            + "; power_dbm=" + item.NextConfig.PowerDbm
                            + "; read_interval_ms=" + item.NextConfig.ReadIntervalMs
                            + "; callback_preserved=true");
                    continue;
                }

                lock (_gate)
                {
                    RuntimeHandle current;
                    if (_runtimes.TryGetValue(item.SerialNumber, out current)
                        && object.ReferenceEquals(current, item.Handle))
                        _runtimes.Remove(item.SerialNumber);
                }
                restart.Add(new RuntimeStopRequest(item.SerialNumber, item.Handle, item.NextConfig));
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
            if (_laneCalibration != null && _laneCalibration.IsActiveForController)
                calibrationReader = _laneCalibration.Reader(serial);
            if (calibrationReader != null)
            {
                config.PowerDbm = NormalizePower(config.DriverKey, calibrationReader.PowerDbm);
                config.ReadIntervalMs = Math.Max(1, Math.Min(60000, calibrationReader.ReadIntervalMs));
                config.TidStartAddress = Math.Max(0, calibrationReader.TidStartAddress);
                config.TidLength = Math.Max(1, calibrationReader.TidLength);
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
            bool parkingRuntimeActive;
            lock (_gate)
            {
                calibration = _laneCalibration;
                parkingRuntimeActive = HasActiveParkingRuntime(_parkingLayouts);
                RuntimeHandle handle;
                if (_runtimes.TryGetValue(detection.SerialNumber, out handle)) applied = handle.Config;
                _recentDetections.Enqueue(detection);
                while (_recentDetections.Count > 500) _recentDetections.Dequeue();
            }

            if (calibration != null && calibration.IsActiveForController)
            {
                var calibrationEvent = new LaneCalibrationEvent
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
                };
                _outbox.EnqueueLaneCalibration(calibrationEvent);
                LogLaneCalibrationRouted(calibrationEvent);
                return;
            }

            if (parkingRuntimeActive)
            {
                _outbox.EnqueueParking(detection);
                return;
            }

            LogDetectionWithoutRuntime(detection);
        }

        private void LogLaneCalibrationRouted(LaneCalibrationEvent evt)
        {
            if (_logger == null || evt == null) return;
            var now = DateTime.UtcNow;
            lock (_gate)
            {
                if (now - _lastLaneCalibrationRouteLogUtc < TimeSpan.FromSeconds(1)) return;
                _lastLaneCalibrationRouteLogUtc = now;
            }
            _logger.Info(
                "lane-calibration-route",
                "RFID detection routed to Lane Calibration outbox",
                "code=" + evt.LaneCalibrationCode
                + "; revision=" + evt.Revision
                + "; serial=" + evt.SerialNumber
                + "; port_no=" + evt.PortNo
                + "; tid=" + evt.Tid);
        }

        private void LogDetectionWithoutRuntime(RfidDetection detection)
        {
            if (_logger == null) return;
            var now = DateTime.UtcNow;
            lock (_gate)
            {
                if (now - _lastNoRuntimeDetectionLogUtc < TimeSpan.FromSeconds(30)) return;
                _lastNoRuntimeDetectionLogUtc = now;
            }
            _logger.Warn(
                "rfid-routing",
                "RFID detection observed but no active runtime context is assigned; event was not queued",
                "serial=" + detection.SerialNumber
                + "; port_no=" + detection.PortNo
                + "; tid=" + detection.Tid
                + "; runtime_mode=Idle");
        }

        private static bool HasActiveParkingRuntime(IEnumerable<ParkingLayoutRuntimeInfo> layouts)
        {
            return (layouts ?? Enumerable.Empty<ParkingLayoutRuntimeInfo>()).Any(value =>
                value != null
                && !string.IsNullOrWhiteSpace(value.Code)
                && (string.Equals(value.State, "operational", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(value.State, "maintenance", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(value.State, "blocked", StringComparison.OrdinalIgnoreCase)));
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

        private static ParkingLayoutRuntimeInfo CloneParkingLayout(ParkingLayoutRuntimeInfo source)
        {
            if (source == null) return new ParkingLayoutRuntimeInfo();
            return new ParkingLayoutRuntimeInfo
            {
                Code = source.Code,
                Name = source.Name,
                State = source.State,
                PublishedRevision = source.PublishedRevision,
                Lanes = (source.Lanes ?? new List<ParkingLaneRuntimeInfo>())
                    .Where(value => value != null)
                    .Select(value => new ParkingLaneRuntimeInfo { Code = value.Code, Name = value.Name })
                    .ToList(),
            };
        }

        private static LaneCalibrationSessionConfig CloneLaneCalibration(LaneCalibrationSessionConfig source)
        {
            if (source == null) return null;
            return new LaneCalibrationSessionConfig
            {
                Available = source.Available,
                LaneCalibrationCode = source.LaneCalibrationCode,
                Status = source.Status,
                DesiredState = source.DesiredState,
                Reason = source.Reason,
                Revision = source.Revision,
                Readers = (source.Readers ?? new List<LaneCalibrationReaderConfig>())
                    .Where(value => value != null)
                    .Select(value => new LaneCalibrationReaderConfig
                    {
                        SerialNumber = value.SerialNumber,
                        PowerDbm = value.PowerDbm,
                        ReadIntervalMs = value.ReadIntervalMs,
                        TidStartAddress = value.TidStartAddress,
                        TidLength = value.TidLength,
                    }).ToList(),
            };
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

        private static bool CanApplyInPlace(ReaderDeviceConfig current, ReaderDeviceConfig next)
        {
            if (current == null || next == null) return false;
            return string.Equals(NormalizeSerial(current.SerialNumber), NormalizeSerial(next.SerialNumber), StringComparison.OrdinalIgnoreCase)
                && string.Equals(current.DriverKey, next.DriverKey, StringComparison.OrdinalIgnoreCase)
                && string.Equals(NormalizeEndpoint(current.Endpoint), NormalizeEndpoint(next.Endpoint), StringComparison.OrdinalIgnoreCase)
                && current.Port == next.Port
                && current.TidStartAddress == next.TidStartAddress
                && current.TidLength == next.TidLength;
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
                    .Select(value => value.SerialNumber + ":" + value.PowerDbm + ":" + value.ReadIntervalMs
                        + ":" + value.TidStartAddress + ":" + value.TidLength));
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

        private sealed class RuntimeUpdateRequest
        {
            public RuntimeUpdateRequest(string serialNumber, RuntimeHandle handle, ReaderDeviceConfig nextConfig)
            {
                SerialNumber = serialNumber;
                Handle = handle;
                NextConfig = nextConfig;
            }
            public string SerialNumber { get; private set; }
            public RuntimeHandle Handle { get; private set; }
            public ReaderDeviceConfig NextConfig { get; private set; }
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
