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
        public string Mode { get { return _readers.CurrentMode; } }
        public string LaneCalibrationCode { get { return _readers.CurrentLaneCalibrationCode; } }

        public event Action StateChanged;

        public void Start()
        {
            lock (_gate)
            {
                if (_running) return;
                _cts = new CancellationTokenSource();
                var token = _cts.Token;
                _running = true;

                _readers.StartCachedConfiguration();

                _tasks.Add(Task.Run(() => RunLoop("heartbeat", HeartbeatOnce, () => TimeSpan.FromSeconds(_settings.HeartbeatIntervalSec), token)));
                _tasks.Add(Task.Run(() => RunLoop("reader-config", PullReaderConfigOnce, () => TimeSpan.FromSeconds(_settings.ReaderConfigIntervalSec), token)));
                _tasks.Add(Task.Run(() => RunLoop("reader-status", ReportReaderStatusOnce, () => TimeSpan.FromSeconds(_settings.ReaderStatusIntervalSec), token)));
                _tasks.Add(Task.Run(() => RunLoop("parking-push", PushDetectionsOnce, () => TimeSpan.FromMilliseconds(_settings.DetectionPushIntervalMs), token)));
                _tasks.Add(Task.Run(() => RunLoop("lane-calibration-pull", PullLaneCalibrationOnce, LaneCalibrationPollInterval, token)));
                _tasks.Add(Task.Run(() => RunLoop("lane-calibration-push", PushLaneCalibrationEventsOnce, () => TimeSpan.FromMilliseconds(_settings.LaneCalibrationPushIntervalMs), token)));
                _tasks.Add(Task.Run(() => RunLoop("cleanup", CleanupOnce, () => TimeSpan.FromSeconds(_settings.CleanupIntervalSec), token)));
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
            _readers.ClearLaneCalibration("controller_stopped");
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
                var retryAfter = _coreApi.GetRateLimitRetryDelay(ex);
                SetConnectionMessage(retryAfter.HasValue
                    ? "Core API throttled; retrying in " + Math.Ceiling(retryAfter.Value.TotalSeconds) + " second(s)"
                    : ex.Message);
                throw;
            }
        }

        public void PullReaderConfigOnce()
        {
            var configs = _coreApi.PullReaderConfigs();
            _readers.ApplyServerConfiguration(configs);
            if (_logger != null) _logger.Info("reader-config", "Reader configuration synchronized", "count=" + configs.Count);
        }

        public void ReportReaderStatusOnce()
        {
            _coreApi.ReportReaderStatus(_readers.GetStatuses());
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
                if (_coreApi.IsRateLimitError(ex)) throw;
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

        public void PullLaneCalibrationOnce()
        {
            var config = _coreApi.PullLaneCalibration(_readers.CurrentLaneCalibrationCode);
            if (config == null || !config.Available || !config.IsRunningDesired)
            {
                _readers.ClearLaneCalibration(config == null ? "no_session" : (config.Status ?? "stopped"));
                NotifyStateChanged();
                return;
            }

            try
            {
                _readers.ApplyLaneCalibrationConfiguration(config);
            }
            catch (Exception ex)
            {
                _readers.ClearLaneCalibration("invalid_configuration");
                _coreApi.ReportLaneCalibrationStatus(
                    config.LaneCalibrationCode,
                    "failed",
                    DateTime.UtcNow,
                    ex.Message);
                if (_logger != null) _logger.Error("lane-calibration", "Lane Calibration configuration rejected", ex);
                NotifyStateChanged();
                return;
            }

            if (string.Equals(config.Status, "ready", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    _coreApi.ReportLaneCalibrationStatus(
                        config.LaneCalibrationCode,
                        "running",
                        DateTime.UtcNow,
                        "Controller started Lane Calibration");
                }
                catch (Exception ex)
                {
                    if (_coreApi.IsRateLimitError(ex)) throw;
                    if (_logger != null) _logger.Warn("lane-calibration", "Could not report Lane Calibration running status; next poll will retry", ex.Message);
                }
            }

            NotifyStateChanged();
        }

        public void PushLaneCalibrationEventsOnce()
        {
            var batch = _store.GetPendingLaneCalibrationEvents(_settings.LaneCalibrationBatchSize);
            if (batch.Count == 0) return;

            var laneCalibrationCode = batch[0].Event.LaneCalibrationCode;
            var sameSession = batch
                .Where(x => string.Equals(x.Event.LaneCalibrationCode, laneCalibrationCode, StringComparison.OrdinalIgnoreCase))
                .Take(Math.Min(100, _settings.LaneCalibrationBatchSize))
                .ToList();
            var ids = sameSession.Select(x => x.Id).ToList();

            try
            {
                var results = _coreApi.PushLaneCalibrationEvents(laneCalibrationCode, sameSession.Select(x => x.Event).ToList());
                var deliveredIds = results.Where(result => result.Delivered).Select(result => sameSession[result.Index].Id).ToList();
                var rejected = results.Where(result => result.Rejected).ToList();
                if (deliveredIds.Count > 0) _store.MarkLaneCalibrationSent(deliveredIds);
                if (rejected.Count > 0)
                {
                    var rejectedIds = rejected.Select(result => sameSession[result.Index].Id).ToList();
                    var error = string.Join("; ", rejected.Select(result => result.Message ?? "rejected").Distinct().ToArray());
                    _store.MarkLaneCalibrationDead(rejectedIds, error);
                    if (_logger != null) _logger.Warn("lane-calibration-push", "Lane Calibration events rejected", "count=" + rejected.Count + "; error=" + error);
                }
                if (_logger != null) _logger.Info("lane-calibration-push", "Lane Calibration event batch delivered", "code=" + laneCalibrationCode + "; delivered=" + deliveredIds.Count + "; rejected=" + rejected.Count);
            }
            catch (Exception ex)
            {
                if (_coreApi.IsRateLimitError(ex)) throw;
                if (_coreApi.IsPermanentRequestError(ex))
                {
                    _store.MarkLaneCalibrationDead(ids, ex.Message);
                    if (_logger != null) _logger.Error("lane-calibration-push", "Permanent API error; Lane Calibration batch moved to dead state", ex);
                    return;
                }
                var attempts = sameSession.Max(x => x.Attempts) + 1;
                _store.MarkLaneCalibrationFailed(ids, ex.Message, attempts);
                throw;
            }
        }

        public void CleanupOnce()
        {
            _store.CleanupSent(_settings.SentDetectionRetentionDays);
        }

        private void RunLoop(string name, Action action, Func<TimeSpan> intervalProvider, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                var delay = WorkerInterval(intervalProvider);
                try
                {
                    action();
                    delay = WorkerInterval(intervalProvider);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    var retryAfter = _coreApi.GetRateLimitRetryDelay(ex);
                    if (retryAfter.HasValue)
                    {
                        if (retryAfter.Value > delay) delay = retryAfter.Value;
                        if (_logger != null)
                            _logger.Warn(name, "Core API request deferred by rate control",
                                "retry_in_sec=" + Math.Ceiling(delay.TotalSeconds) + "; reason=" + ex.Message);
                    }
                    else if (_logger != null)
                    {
                        _logger.Warn(name, "Worker iteration failed", ex.Message);
                    }
                }

                if (token.WaitHandle.WaitOne(delay)) return;
            }
        }

        private TimeSpan LaneCalibrationPollInterval()
        {
            return TimeSpan.FromSeconds(string.IsNullOrWhiteSpace(_readers.CurrentLaneCalibrationCode)
                ? _settings.LaneCalibrationIdlePollIntervalSec
                : _settings.LaneCalibrationActivePollIntervalSec);
        }

        private static TimeSpan WorkerInterval(Func<TimeSpan> intervalProvider)
        {
            var value = intervalProvider == null ? TimeSpan.FromSeconds(1) : intervalProvider();
            if (value < TimeSpan.FromMilliseconds(100)) return TimeSpan.FromMilliseconds(100);
            if (value > TimeSpan.FromDays(1)) return TimeSpan.FromDays(1);
            return value;
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
