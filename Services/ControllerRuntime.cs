using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NSPGatekeeper.Controller.Configuration;
using NSPGatekeeper.Controller.Domain;
using NSPGatekeeper.Controller.Infrastructure.Database;
using NSPGatekeeper.Controller.Infrastructure.Logging;
using NSPGatekeeper.Controller.Integration.CoreApi;

namespace NSPGatekeeper.Controller.Services
{
    public sealed class ControllerRuntime : IDisposable
    {
        private readonly AppSettings _settings;
        private readonly LocalStore _store;
        private readonly CoreApiClient _coreApi;
        private readonly ReaderManager _readers;
        private readonly FileLogger _logger;
        private readonly object _gate = new object();
        private readonly List<Task> _tasks = new List<Task>();
        private CancellationTokenSource _cts;
        private bool _running;
        private string _connectionMessage = "stopped";

        public ControllerRuntime(AppSettings settings, LocalStore store, CoreApiClient coreApi, ReaderManager readers, FileLogger logger)
        {
            _settings = settings ?? throw new ArgumentNullException("settings");
            _store = store ?? throw new ArgumentNullException("store");
            _coreApi = coreApi ?? throw new ArgumentNullException("coreApi");
            _readers = readers ?? throw new ArgumentNullException("readers");
            _logger = logger;
        }

        public bool Running { get { lock (_gate) return _running; } }
        public string ConnectionMessage { get { lock (_gate) return _connectionMessage; } }
        public string ServerUrl { get { return _coreApi.BaseUrl; } }
        public string Mode { get { return _readers.CurrentMode; } }
        public string MeasurementCode { get { return _readers.CurrentMeasurementCode; } }

        public event Action StateChanged;

        public void Start()
        {
            lock (_gate)
            {
                if (_running) return;
                _cts = new CancellationTokenSource();
                var token = _cts.Token;
                _running = true;

                // Cached technical Reader profiles allow local startup while Edge is temporarily unavailable.
                _readers.StartCachedConfiguration();

                _tasks.Add(Task.Run(() => RunLoop("heartbeat", HeartbeatOnce, TimeSpan.FromSeconds(_settings.HeartbeatIntervalSec), token)));
                _tasks.Add(Task.Run(() => RunLoop("device-config", PullDeviceConfigOnce, TimeSpan.FromSeconds(_settings.DeviceConfigIntervalSec), token)));
                _tasks.Add(Task.Run(() => RunLoop("device-status", ReportDeviceStatusOnce, TimeSpan.FromSeconds(_settings.DeviceStatusIntervalSec), token)));
                _tasks.Add(Task.Run(() => RunLoop("parking-push", PushDetectionsOnce, TimeSpan.FromMilliseconds(_settings.DetectionPushIntervalMs), token)));
                _tasks.Add(Task.Run(() => RunLoop("measurement-pull", PullMeasurementOnce, TimeSpan.FromSeconds(_settings.MeasurementPollIntervalSec), token)));
                _tasks.Add(Task.Run(() => RunLoop("measurement-push", PushMeasurementEventsOnce, TimeSpan.FromMilliseconds(_settings.MeasurementPushIntervalMs), token)));
                _tasks.Add(Task.Run(() => RunLoop("cleanup", CleanupOnce, TimeSpan.FromSeconds(_settings.CleanupIntervalSec), token)));
            }
            NotifyStateChanged();
        }

        public void Stop()
        {
            CancellationTokenSource cts;
            Task[] tasks;
            lock (_gate)
            {
                if (!_running) return;
                _running = false;
                cts = _cts;
                _cts = null;
                tasks = _tasks.ToArray();
                _tasks.Clear();
            }
            try { if (cts != null) cts.Cancel(); } catch { }
            try { Task.WaitAll(tasks, 4000); } catch { }
            _readers.ClearMeasurement("controller_stopped");
            NotifyStateChanged();
        }

        public void ResetConnection()
        {
            _coreApi.InvalidateToken();
            SetConnectionMessage("connection_settings_changed");
        }

        public void TestConnectionOnce()
        {
            try
            {
                _coreApi.EnsureAuthenticated();
                SetConnectionMessage("Core API authenticated");
            }
            catch (Exception ex)
            {
                SetConnectionMessage(ex.Message);
                throw;
            }
        }

        public void HeartbeatOnce()
        {
            try
            {
                _coreApi.EnsureAuthenticated();
                var response = _coreApi.Heartbeat();
                SetConnectionMessage((string)response["message"] ?? "connected");
            }
            catch (Exception ex)
            {
                SetConnectionMessage(ex.Message);
                throw;
            }
        }

        public void PullDeviceConfigOnce()
        {
            var configs = _coreApi.PullDeviceConfigs();
            _readers.ApplyServerConfiguration(configs);
            if (_logger != null) _logger.Info("device-config", "Reader configuration synchronized", "count=" + configs.Count);
        }

        public void ReportDeviceStatusOnce()
        {
            _coreApi.ReportDeviceStatus(_readers.GetStatuses());
        }

        public void PushDetectionsOnce()
        {
            var batch = _store.GetPendingDetections(_settings.DetectionBatchSize);
            if (batch.Count == 0) return;
            var ids = batch.Select(x => x.Id).ToList();
            try
            {
                _coreApi.PushDetections(batch.Select(x => x.Detection).ToList());
                _store.MarkSent(ids);
                if (_logger != null) _logger.Info("parking-push", "RFID detection batch pushed", "count=" + batch.Count);
            }
            catch (Exception ex)
            {
                if (_coreApi.IsPermanentRequestError(ex))
                {
                    _store.MarkDead(ids, ex.Message);
                    if (_logger != null) _logger.Error("parking-push", "Permanent API error; batch moved to dead state", ex);
                    return;
                }
                var attempts = batch.Max(x => x.Attempts) + 1;
                _store.MarkFailed(ids, ex.Message, attempts);
                throw;
            }
        }

        public void PullMeasurementOnce()
        {
            var currentCode = _readers.CurrentMeasurementCode;
            var config = _coreApi.PullMeasurement(currentCode);
            if (config == null || !config.Available)
            {
                _readers.ClearMeasurement("no_session");
                return;
            }

            if (!config.IsRunningDesired)
            {
                _readers.ClearMeasurement(config.Status ?? "stopped");
                NotifyStateChanged();
                return;
            }

            var now = DateTime.UtcNow;
            if (config.PlannedEndAtUtc.HasValue && config.PlannedEndAtUtc.Value <= now)
            {
                // Controller is the Measurement executor. When a scheduled Measurement
                // window ends while the server still exposes it as ready/running, close it
                // exactly once on the server and restore Parking mode locally.
                _coreApi.ReportMeasurementStatus(config.MeasurementCode, "completed", now, "Measurement window completed by Controller");
                _readers.ClearMeasurement("completed");
                NotifyStateChanged();
                return;
            }

            if (string.Equals(config.Status, "ready", StringComparison.OrdinalIgnoreCase) &&
                config.PlannedStartAtUtc.HasValue && config.PlannedStartAtUtc.Value > now)
            {
                // Session is released to this Controller but its scheduled window has not
                // started yet. Keep normal Parking processing until planned_start_at.
                if (string.Equals(currentCode, config.MeasurementCode, StringComparison.OrdinalIgnoreCase))
                    _readers.ClearMeasurement("waiting_planned_start");
                NotifyStateChanged();
                return;
            }

            var isNew = !string.Equals(currentCode, config.MeasurementCode, StringComparison.OrdinalIgnoreCase);
            _readers.ApplyMeasurementConfiguration(config);

            // Ready -> Running is explicitly reported once. Subsequent polls receive status=running.
            if (isNew && string.Equals(config.Status, "ready", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    _coreApi.ReportMeasurementStatus(config.MeasurementCode, "running", now, "Controller started Measurement mode");
                }
                catch (Exception ex)
                {
                    if (_logger != null) _logger.Warn("measurement", "Could not report running status", ex.Message);
                }
            }

            NotifyStateChanged();
        }

        public void PushMeasurementEventsOnce()
        {
            var batch = _store.GetPendingMeasurementEvents(_settings.MeasurementBatchSize);
            if (batch.Count == 0) return;

            // The API contract accepts one measurement_code per request. Keep old pending
            // sessions isolated so a reconnect never mixes events from different sessions.
            var measurementCode = batch[0].Event.MeasurementCode;
            var sameSession = batch
                .Where(x => string.Equals(x.Event.MeasurementCode, measurementCode, StringComparison.OrdinalIgnoreCase))
                .Take(Math.Min(100, _settings.MeasurementBatchSize))
                .ToList();
            var ids = sameSession.Select(x => x.Id).ToList();

            try
            {
                _coreApi.PushMeasurementEvents(measurementCode, sameSession.Select(x => x.Event).ToList());
                _store.MarkMeasurementSent(ids);
                if (_logger != null) _logger.Info("measurement-push", "Measurement event batch pushed", "code=" + measurementCode + "; count=" + sameSession.Count);
            }
            catch (Exception ex)
            {
                if (_coreApi.IsPermanentRequestError(ex))
                {
                    _store.MarkMeasurementDead(ids, ex.Message);
                    if (_logger != null) _logger.Error("measurement-push", "Permanent API error; Measurement batch moved to dead state", ex);
                    return;
                }
                var attempts = sameSession.Max(x => x.Attempts) + 1;
                _store.MarkMeasurementFailed(ids, ex.Message, attempts);
                throw;
            }
        }

        public void CleanupOnce()
        {
            _store.CleanupSent(_settings.SentDetectionRetentionDays);
        }

        private void RunLoop(string name, Action action, TimeSpan interval, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try { action(); }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    if (_logger != null) _logger.Warn(name, "Worker iteration failed", ex.Message);
                }

                if (token.WaitHandle.WaitOne(interval)) return;
            }
        }

        private void SetConnectionMessage(string value)
        {
            lock (_gate) _connectionMessage = string.IsNullOrWhiteSpace(value) ? "connected" : value;
            NotifyStateChanged();
        }

        private void NotifyStateChanged()
        {
            var handler = StateChanged;
            if (handler != null)
            {
                try { handler(); } catch { }
            }
        }

        public void Dispose()
        {
            Stop();
            _readers.Dispose();
            _coreApi.Dispose();
        }
    }
}
