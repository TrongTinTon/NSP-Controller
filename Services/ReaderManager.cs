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
        private MeasurementSessionConfig _measurement;
        private HashSet<string> _measurementReaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> _measurementPairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool _disposed;

        public ReaderManager(ReaderDriverRegistry registry, LocalStore store, DetectionOutboxWriter outbox, FileLogger logger, AppSettings settings)
        {
            _registry = registry ?? throw new ArgumentNullException("registry");
            _store = store ?? throw new ArgumentNullException("store");
            _outbox = outbox ?? throw new ArgumentNullException("outbox");
            _logger = logger;
            _settings = settings ?? throw new ArgumentNullException("settings");
        }

        public event Action<RfidDetection> DetectionObserved;
        public event Action<ReaderStatus> StatusObserved;

        public string CurrentMeasurementCode
        {
            get
            {
                lock (_gate) return _measurement == null ? string.Empty : (_measurement.MeasurementCode ?? string.Empty);
            }
        }

        public string CurrentMode
        {
            get
            {
                lock (_gate) return _measurement != null && _measurement.IsRunningDesired ? "Measurement" : "Parking";
            }
        }

        public int CurrentMeasurementRevision
        {
            get
            {
                lock (_gate) return _measurement == null ? 0 : _measurement.Revision;
            }
        }


        public void StartCachedConfiguration()
        {
            ApplyRuntimeConfiguration(_store.GetDeviceConfigs());
        }

        public void ReloadCachedConfiguration()
        {
            ApplyRuntimeConfiguration(_store.GetDeviceConfigs());
        }

        public void ApplyServerConfiguration(IList<ReaderDeviceConfig> serverConfigs)
        {
            serverConfigs = serverConfigs ?? new List<ReaderDeviceConfig>();
            var cached = _store.GetDeviceConfigs()
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

            foreach (var config in merged) _store.UpsertDeviceConfig(config);
            _store.DisableDevicesNotIn(merged.Select(x => x.SerialNumber).ToList());

            MeasurementSessionConfig measurement;
            lock (_gate) measurement = _measurement;
            ApplyRuntimeConfiguration(BuildEffectiveConfigs(merged, measurement));
        }

        public void ApplyMeasurementConfiguration(MeasurementSessionConfig config)
        {
            if (config == null || !config.IsRunningDesired || string.IsNullOrWhiteSpace(config.MeasurementCode))
            {
                ClearMeasurement("server_stopped");
                return;
            }

            config.MeasurementCode = (config.MeasurementCode ?? string.Empty).Trim().ToUpperInvariant();
            if (config.Revision <= 0) config.Revision = 1;
            config.Readers = (config.Readers ?? new List<MeasurementReaderConfig>())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.SerialNumber))
                .Select(x => new MeasurementReaderConfig
                {
                    SerialNumber = x.SerialNumber.Trim().ToUpperInvariant(),
                    PowerDbm = Math.Max(0, Math.Min(40, x.PowerDbm)),
                    ReadIntervalMs = Math.Max(1, Math.Min(60000, x.ReadIntervalMs)),
                    Antennas = (x.Antennas ?? new List<int>())
                        .Where(number => number > 0)
                        .Distinct()
                        .OrderBy(number => number)
                        .ToList()
                })
                .Where(x => x.Antennas.Count > 0)
                .GroupBy(x => x.SerialNumber, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(x => x.SerialNumber, StringComparer.OrdinalIgnoreCase)
                .ToList();

            bool changed;
            lock (_gate)
            {
                var newPairs = new HashSet<string>(
                    config.Readers.SelectMany(reader =>
                        reader.Antennas.Select(antennaNo => PairKey(reader.SerialNumber, antennaNo))),
                    StringComparer.OrdinalIgnoreCase);
                var newReaders = new HashSet<string>(
                    config.Readers.Select(reader => reader.SerialNumber),
                    StringComparer.OrdinalIgnoreCase);
                changed = _measurement == null
                          || !string.Equals(_measurement.MeasurementCode, config.MeasurementCode, StringComparison.OrdinalIgnoreCase)
                          || _measurement.Revision != config.Revision
                          || !string.Equals(MeasurementReaderSignature(_measurement), MeasurementReaderSignature(config), StringComparison.Ordinal);

                _measurement = config;
                _measurementReaders = newReaders;
                _measurementPairs = newPairs;
            }

            if (changed)
            {
                ApplyRuntimeConfiguration(BuildEffectiveConfigs(_store.GetDeviceConfigs(), config));
                if (_logger != null)
                    _logger.Info(
                        "measurement",
                        "Measurement runtime applied",
                        "code=" + config.MeasurementCode
                        + "; revision=" + config.Revision
                        + "; readers=" + config.Readers.Count
                        + "; antennas=" + _measurementPairs.Count);
            }
        }


        private static string MeasurementReaderSignature(MeasurementSessionConfig config)
        {
            if (config == null) return string.Empty;
            return string.Join(";",
                (config.Readers ?? new List<MeasurementReaderConfig>())
                    .Where(reader => reader != null)
                    .OrderBy(reader => reader.SerialNumber, StringComparer.OrdinalIgnoreCase)
                    .Select(reader =>
                        (reader.SerialNumber ?? string.Empty).Trim().ToUpperInvariant()
                        + "|P" + reader.PowerDbm
                        + "|I" + reader.ReadIntervalMs
                        + "|A" + string.Join(",", (reader.Antennas ?? new List<int>()).OrderBy(number => number))));
        }

        public void ClearMeasurement(string reason)
        {
            bool cleared;
            lock (_gate) cleared = ClearMeasurementLocked(reason);
            if (cleared && !_disposed)
                ApplyRuntimeConfiguration(_store.GetDeviceConfigs());
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
            incoming.DeviceCode = incoming.SerialNumber;
            if (local != null)
            {
                if (string.IsNullOrWhiteSpace(incoming.DriverKey)) incoming.DriverKey = local.DriverKey;
                if (string.IsNullOrWhiteSpace(incoming.Endpoint)) incoming.Endpoint = local.Endpoint;
                if (incoming.Port <= 0) incoming.Port = local.Port;
                if (string.IsNullOrWhiteSpace(incoming.Model)) incoming.Model = local.Model;
                if (string.IsNullOrWhiteSpace(incoming.DeviceName)) incoming.DeviceName = local.DeviceName;

                if (local.Options != null)
                {
                    foreach (var pair in local.Options)
                        if (!incoming.Options.ContainsKey(pair.Key)) incoming.Options[pair.Key] = pair.Value;
                }
            }

            if (string.IsNullOrWhiteSpace(incoming.DriverKey)) incoming.DriverKey = "cf-e718";
            if (string.IsNullOrWhiteSpace(incoming.Endpoint) &&
                string.Equals(incoming.DriverKey, "cf-e718", StringComparison.OrdinalIgnoreCase) &&
                !incoming.Options.ContainsKey("connection"))
            {
                // CF-E718 native SDK can auto-open a serial/COM reader when no endpoint is known.
                incoming.Options["connection"] = "com";
            }
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
                            DeviceCode = serial,
                            DriverKey = config.DriverKey,
                            SerialNumber = serial,
                            Model = config.Model,
                            Endpoint = config.Endpoint,
                            Online = false,
                            Message = ex.Message,
                            ConfigRevision = config.ConfigRevision,
                            PowerDbm = config.PowerDbm,
                            ReadIntervalMs = config.ReadIntervalMs,
                            Antennas = config.AntennaNumbers(),
                            UpdatedAtUtc = DateTime.UtcNow
                        });
                    }
                }
            }
        }

        private void OnDetection(RfidDetection detection)
        {
            if (detection == null || string.IsNullOrWhiteSpace(detection.DeviceSerial) || detection.AntennaId <= 0 || string.IsNullOrWhiteSpace(detection.Tid)) return;
            detection.ControllerCode = _settings.ControllerCode ?? string.Empty;
            detection.DeviceSerial = detection.DeviceSerial.Trim().ToUpperInvariant();
            detection.DeviceCode = detection.DeviceSerial;
            detection.Tid = detection.Tid.Trim().ToUpperInvariant();
            detection.EventUid = BuildEventUid("RFID");

            bool measurementReader;
            bool measurementPair;
            MeasurementSessionConfig measurement;
            lock (_gate)
            {
                measurement = _measurement;
                measurementReader = measurement != null && measurement.IsRunningDesired && _measurementReaders.Contains(detection.DeviceSerial);
                measurementPair = measurementReader && _measurementPairs.Contains(PairKey(detection.DeviceSerial, detection.AntennaId));

                _recentDetections.Enqueue(detection);
                while (_recentDetections.Count > 500) _recentDetections.Dequeue();
            }

            try
            {
                if (measurementReader)
                {
                    // Measurement scope changes only Reader runtime settings. The Controller
                    // reports every TID seen on the selected Reader/Antenna; Edge owns target
                    // matching and all Measurement business validation.
                    if (measurementPair)
                    {
                        var readerConfig = measurement.Reader(detection.DeviceSerial);
                        _outbox.EnqueueMeasurement(new MeasurementEvent
                        {
                            EventUid = BuildEventUid("MEAS"),
                            MeasurementCode = measurement.MeasurementCode,
                            Revision = measurement.Revision,
                            PowerDbm = readerConfig == null ? 0 : readerConfig.PowerDbm,
                            ReadIntervalMs = readerConfig == null ? 200 : readerConfig.ReadIntervalMs,
                            SerialNumber = detection.DeviceSerial,
                            AntennaNo = detection.AntennaId,
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

            var handler = DetectionObserved;
            if (handler != null) handler(detection);
        }

        private void OnStatus(ReaderStatus status)
        {
            if (status == null) return;
            lock (_gate)
            {
                RuntimeHandle handle;
                var serial = (status.SerialNumber ?? status.DeviceCode ?? string.Empty).Trim().ToUpperInvariant();
                if (_runtimes.TryGetValue(serial, out handle))
                {
                    status.DeviceCode = serial;
                    status.SerialNumber = serial;
                    status.PowerDbm = handle.Config.PowerDbm;
                    status.ReadIntervalMs = handle.Config.ReadIntervalMs;
                    status.Antennas = handle.Config.AntennaNumbers();
                }
            }
            try { _store.UpsertReaderStatus(status); }
            catch (Exception ex) { if (_logger != null) _logger.Error("reader-status", "Could not persist reader status", ex); }
            var handlerStatus = StatusObserved;
            if (handlerStatus != null) handlerStatus(status);
        }

        private string BuildEventUid(string prefix)
        {
            var controller = string.IsNullOrWhiteSpace(_settings.ControllerCode) ? "CTRL" : _settings.ControllerCode.Trim().ToUpperInvariant();
            return controller + "-" + prefix + "-" + Guid.NewGuid().ToString("N");
        }

        private IList<ReaderDeviceConfig> BuildEffectiveConfigs(IList<ReaderDeviceConfig> baseConfigs, MeasurementSessionConfig measurement)
        {
            baseConfigs = baseConfigs ?? new List<ReaderDeviceConfig>();
            if (measurement == null || !measurement.IsRunningDesired) return baseConfigs;

            var selectedReaders = (measurement.Readers ?? new List<MeasurementReaderConfig>())
                .Where(reader => reader != null && !string.IsNullOrWhiteSpace(reader.SerialNumber))
                .ToDictionary(
                    reader => reader.SerialNumber.Trim().ToUpperInvariant(),
                    reader => reader,
                    StringComparer.OrdinalIgnoreCase);

            return baseConfigs.Select(config =>
            {
                if (config == null) return config;
                var serial = (config.SerialNumber ?? string.Empty).Trim().ToUpperInvariant();
                MeasurementReaderConfig measurementReader;
                if (!selectedReaders.TryGetValue(serial, out measurementReader)) return config;

                var selectedAntennas = new HashSet<int>(
                    measurementReader.Antennas ?? new List<int>());
                var clone = CloneReaderConfig(config);
                clone.PowerDbm = Math.Max(0, Math.Min(40, measurementReader.PowerDbm));
                clone.ReadIntervalMs = Math.Max(1, Math.Min(60000, measurementReader.ReadIntervalMs));
                foreach (var antenna in clone.Antennas ?? new List<ReaderAntennaConfig>())
                    antenna.Enabled = selectedAntennas.Contains(antenna.AntennaId);
                clone.ConfigHash = (config.ConfigHash ?? string.Empty)
                                   + "|MEAS|" + measurement.MeasurementCode
                                   + "|R" + measurement.Revision
                                   + "|P" + clone.PowerDbm
                                   + "|I" + clone.ReadIntervalMs
                                   + "|A" + string.Join(",", clone.AntennaNumbers());
                return clone;
            }).Where(x => x != null).ToList();
        }

        private static ReaderDeviceConfig CloneReaderConfig(ReaderDeviceConfig source)
        {
            var clone = new ReaderDeviceConfig
            {
                DeviceCode = source.DeviceCode,
                DriverKey = source.DriverKey,
                DeviceName = source.DeviceName,
                SerialNumber = source.SerialNumber,
                Model = source.Model,
                Endpoint = source.Endpoint,
                Port = source.Port,
                Enabled = source.Enabled,
                ConfigRevision = source.ConfigRevision,
                ConfigHash = source.ConfigHash,
                PowerDbm = source.PowerDbm,
                ReadIntervalMs = source.ReadIntervalMs,
                TidStartAddress = source.TidStartAddress,
                TidLength = source.TidLength,
                Antennas = (source.Antennas ?? new List<ReaderAntennaConfig>())
                    .Where(x => x != null)
                    .Select(x => new ReaderAntennaConfig
                    {
                        AntennaId = x.AntennaId,
                        Enabled = x.Enabled
                    }).ToList(),
                Options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            };
            if (source.Options != null)
            {
                foreach (var pair in source.Options) clone.Options[pair.Key] = pair.Value;
            }
            return clone;
        }

        private static string PairKey(string serial, int antennaNo)
        {
            return (serial ?? string.Empty).Trim().ToUpperInvariant() + "|" + antennaNo;
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

        private bool ClearMeasurementLocked(string reason)
        {
            if (_measurement == null) return false;
            var code = _measurement.MeasurementCode;
            _measurement = null;
            _measurementReaders.Clear();
            _measurementPairs.Clear();
            if (_logger != null) _logger.Info("measurement", "Measurement mode cleared; Parking mode restored", "code=" + code + "; reason=" + (reason ?? "stopped"));
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
                ClearMeasurementLocked("controller_stopped");
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
