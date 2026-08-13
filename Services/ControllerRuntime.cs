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
        private readonly object _readerConfigGate = new object();
        private readonly List<Task> _tasks = new List<Task>();
        private CancellationTokenSource _cts;
        private bool _running;
        private bool _readerConfigSynchronized;
        private string _connectionMessage = "stopped";
        private string _lastLaneCalibrationPullSignature = string.Empty;
        private DateTime _lastLaneCalibrationSuccessfulPullUtc = DateTime.MinValue;

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
        public ControllerRuntimeContextSnapshot RuntimeContext { get { return _readers.GetRuntimeContextSnapshot(); } }

        public event Action StateChanged;

        public void Start()
        {
            lock (_gate)
            {
                if (_running) return;
                _cts = new CancellationTokenSource();
                var token = _cts.Token;
                _running = true;
                lock (_readerConfigGate) _readerConfigSynchronized = false;

                _readers.StartCachedConfiguration();

                _tasks.Add(Task.Run(() => RunLoop("heartbeat", HeartbeatOnce, () => TimeSpan.FromSeconds(_settings.HeartbeatIntervalSec), token)));
                _tasks.Add(Task.Run(() => RunLoop("reader-config", PullReaderConfigOnce, () => TimeSpan.FromSeconds(_settings.ReaderConfigIntervalSec), token)));
                _tasks.Add(Task.Run(() => RunLoop("reader-discovery", _readers.DiscoverReadersOnce, () => TimeSpan.FromSeconds(_settings.ReaderDiscoveryIntervalSec), token)));
                _tasks.Add(Task.Run(() => RunLoop("reader-status", ReportReaderStatusOnce, () => TimeSpan.FromSeconds(_settings.ReaderStatusIntervalSec), token)));
                _tasks.Add(Task.Run(() => RunLoop("parking-push", PushDetectionsOnce, () => TimeSpan.FromMilliseconds(_settings.DetectionPushIntervalMs), token)));
                _tasks.Add(Task.Run(() => RunLoop("lane-calibration-pull", PullLaneCalibrationOnce, LaneCalibrationPollInterval, token)));
                _tasks.Add(Task.Run(() => RunLoop("lane-calibration-push", PushLaneCalibrationEventsOnce, () => TimeSpan.FromMilliseconds(_settings.LaneCalibrationPushIntervalMs), token)));
                _tasks.Add(Task.Run(() => RunLoop("cleanup", CleanupOnce, () => TimeSpan.FromSeconds(_settings.CleanupIntervalSec), token)));
            }
            if (_logger != null)
                _logger.Info(
                    "runtime",
                    "Controller workers started",
                    "heartbeat_sec=" + _settings.HeartbeatIntervalSec
                    + "; reader_config_sec=" + _settings.ReaderConfigIntervalSec
                    + "; reader_discovery_sec=" + _settings.ReaderDiscoveryIntervalSec
                    + "; reader_status_sec=" + _settings.ReaderStatusIntervalSec
                    + "; lane_calibration_idle_sec=" + _settings.LaneCalibrationIdlePollIntervalSec
                    + "; lane_calibration_active_sec=" + _settings.LaneCalibrationActivePollIntervalSec
                    + "; lane_calibration_lease_sec=" + _settings.LaneCalibrationLeaseTimeoutSec);
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
            if (cts != null) cts.Cancel();
            try
            {
                if (!Task.WaitAll(tasks, 4000) && _logger != null)
                    _logger.Warn("runtime", "Controller workers did not stop before timeout", "worker_count=" + tasks.Length);
            }
            catch (AggregateException ex)
            {
                if (_logger != null) _logger.Error("runtime", "Controller worker shutdown returned errors", ex);
            }
            _readers.ClearLaneCalibration("controller_stopped");
            if (_logger != null) _logger.Info("runtime", "Controller workers stopped");
            NotifyStateChanged();
        }

        public void ResetConnection()
        {
            _coreApi.InvalidateToken();
            lock (_readerConfigGate) _readerConfigSynchronized = false;
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
            lock (_readerConfigGate)
            {
                SynchronizeReaderConfigurationLocked();
            }
        }

        private void EnsureReaderConfigurationSynchronized()
        {
            lock (_readerConfigGate)
            {
                if (_readerConfigSynchronized) return;
                SynchronizeReaderConfigurationLocked();
            }
        }

        private void SynchronizeReaderConfigurationLocked()
        {
            var snapshot = _coreApi.PullControllerRuntimeConfiguration();
            _readers.ApplyServerConfiguration(snapshot);
            _readerConfigSynchronized = true;
            if (_logger != null)
                _logger.Info(
                    "reader-config",
                    "Controller runtime configuration synchronized",
                    "reader_count=" + snapshot.Devices.Count);
            NotifyStateChanged();
        }

        public void ReportReaderStatusOnce()
        {
            _coreApi.ReportReaderStatus(_readers.GetStatuses());
        }

        public void PushDetectionsOnce()
        {
            // ReaderManager routes each physical observation to exactly one outbox:
            // a Reader explicitly scoped by an active Calibration Session goes to
            // Calibration; every other Reader continues normal raw Parking acquisition.
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
            try
            {
                var config = _coreApi.PullLaneCalibration(_readers.CurrentLaneCalibrationCode);
                _lastLaneCalibrationSuccessfulPullUtc = DateTime.UtcNow;
                LogLaneCalibrationPullState(config);
                if (config == null || !config.Available || !config.IsActiveForController)
                {
                    _readers.ClearLaneCalibration(config == null ? "no_session" : (config.Status ?? "stopped"));
                    NotifyStateChanged();
                    return;
                }

                // Ready and running sessions are execution contexts supplied by Edge.
                // The explicit Reader list is an execution scope, not a Parking/Lane decision.
                // Only matching SDK Serials receive Calibration acquisition parameters.
                _readers.ApplyLaneCalibrationConfiguration(config);
                NotifyStateChanged();

                // Released is represented as Edge status=ready. Once the Controller
                // has accepted and applied the execution scope, report running
                // immediately instead of waiting for the first RFID detection.
                // If the acknowledgement is lost, the next pull still returns ready
                // and retries this idempotent transition.
                if (string.Equals(config.Status, "ready", StringComparison.OrdinalIgnoreCase))
                {
                    _coreApi.ReportLaneCalibrationStatus(
                        config.LaneCalibrationCode,
                        config.Revision,
                        "running",
                        DateTime.UtcNow,
                        "Lane Calibration acquisition mode applied by Controller.");
                    if (_logger != null)
                        _logger.Info(
                            "lane-calibration-status",
                            "Lane Calibration running status acknowledged by Edge",
                            "code=" + config.LaneCalibrationCode + "; revision=" + config.Revision);
                }
            }
            catch
            {
                ExpireLaneCalibrationLeaseIfNeeded();
                throw;
            }
        }

        private void ExpireLaneCalibrationLeaseIfNeeded()
        {
            if (string.IsNullOrWhiteSpace(_readers.CurrentLaneCalibrationCode)) return;
            if (_lastLaneCalibrationSuccessfulPullUtc == DateTime.MinValue) return;
            if (DateTime.UtcNow - _lastLaneCalibrationSuccessfulPullUtc
                <= TimeSpan.FromSeconds(_settings.LaneCalibrationLeaseTimeoutSec)) return;

            var code = _readers.CurrentLaneCalibrationCode;
            _readers.ClearLaneCalibration("lease_expired");
            if (_logger != null)
                _logger.Warn(
                    "lane-calibration-lease",
                    "Lane Calibration execution lease expired; Readers returned to normal acquisition",
                    "code=" + code + "; timeout_sec=" + _settings.LaneCalibrationLeaseTimeoutSec);
            NotifyStateChanged();
        }

        private void LogLaneCalibrationPullState(LaneCalibrationSessionConfig config)
        {
            var signature = config == null
                ? "null"
                : string.Join("|", new[]
                {
                    config.Available ? "available" : "unavailable",
                    config.LaneCalibrationCode ?? string.Empty,
                    config.Status ?? string.Empty,
                    config.DesiredState ?? string.Empty,
                    config.Reason ?? string.Empty,
                    config.Revision.ToString(),
                    config.IsActiveForController ? "active" : "inactive",
                });
            if (string.Equals(signature, _lastLaneCalibrationPullSignature, StringComparison.Ordinal)) return;
            _lastLaneCalibrationPullSignature = signature;
            if (_logger == null) return;
            _logger.Info(
                "lane-calibration-pull",
                "Lane Calibration runtime state changed",
                config == null
                    ? "available=false; reason=null_response"
                    : "available=" + config.Available
                      + "; code=" + (config.LaneCalibrationCode ?? string.Empty)
                      + "; status=" + (config.Status ?? string.Empty)
                      + "; desired_state=" + (config.DesiredState ?? string.Empty)
                      + "; reason=" + (config.Reason ?? string.Empty)
                      + "; revision=" + config.Revision
                      + "; active_for_controller=" + config.IsActiveForController);
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
                _coreApi.PushLaneCalibrationEvents(
                    laneCalibrationCode,
                    sameSession.Select(x => x.Event).ToList());

                // Transport ACK only: a successful Core API call means Edge received
                // the complete submitted batch. Edge/Cloud own validation, idempotency,
                // filtering, persistence and synchronization decisions.
                _store.MarkLaneCalibrationSent(ids);

                if (_logger != null)
                    _logger.Info(
                        "lane-calibration-push",
                        "Lane Calibration raw event batch acknowledged",
                        "code=" + laneCalibrationCode
                        + "; count=" + ids.Count
                        + "; http=200");
            }
            catch (Exception ex)
            {
                var attempts = sameSession.Max(x => x.Attempts) + 1;
                _store.MarkLaneCalibrationFailed(ids, ex.Message, attempts);
                if (_logger != null)
                    _logger.Warn(
                        "lane-calibration-push",
                        "Lane Calibration raw event batch was not acknowledged; outbox retained for retry",
                        "code=" + laneCalibrationCode + "; count=" + ids.Count + "; attempts=" + attempts + "; error=" + ex.Message);
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
                        _logger.Error(name, "Worker iteration failed", ex);
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
            if (handler == null) return;
            try
            {
                handler();
            }
            catch (Exception ex)
            {
                if (_logger != null) _logger.Error("runtime", "StateChanged subscriber failed", ex);
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
