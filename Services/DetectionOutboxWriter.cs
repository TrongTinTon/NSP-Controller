using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using NSPGatekeeper.Controller.Domain;
using NSPGatekeeper.Controller.Infrastructure.Database;
using NSPGatekeeper.Controller.Infrastructure.Logging;

namespace NSPGatekeeper.Controller.Services
{
    public sealed class DetectionOutboxWriter : IDisposable
    {
        private readonly LocalStore _store;
        private readonly FileLogger _logger;
        private readonly BlockingCollection<RfidDetection> _parkingQueue = new BlockingCollection<RfidDetection>(20000);
        private readonly BlockingCollection<LaneCalibrationEvent> _laneCalibrationQueue = new BlockingCollection<LaneCalibrationEvent>(10000);
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly Thread _parkingThread;
        private readonly Thread _laneCalibrationThread;
        private bool _disposed;

        public DetectionOutboxWriter(LocalStore store, FileLogger logger)
        {
            _store = store ?? throw new ArgumentNullException("store");
            _logger = logger;
            _parkingThread = new Thread(RunParking) { IsBackground = true, Name = "Parking outbox writer" };
            _laneCalibrationThread = new Thread(RunLaneCalibration) { IsBackground = true, Name = "Lane Calibration outbox writer" };
            _parkingThread.Start();
            _laneCalibrationThread.Start();
        }

        public void EnqueueParking(RfidDetection detection)
        {
            if (detection == null || _disposed) return;
            if (_parkingQueue.TryAdd(detection)) return;

            if (_logger != null) _logger.Warn("parking-outbox", "Ingest queue full; using synchronous persistence", "serial=" + detection.SerialNumber);
            _store.EnqueueDetections(new List<RfidDetection> { detection });
        }

        public void EnqueueLaneCalibration(LaneCalibrationEvent evt)
        {
            if (evt == null || _disposed) return;
            if (_laneCalibrationQueue.TryAdd(evt)) return;

            if (_logger != null) _logger.Warn("lane-calibration-outbox", "Ingest queue full; using synchronous persistence", "serial=" + evt.SerialNumber);
            _store.EnqueueLaneCalibrationEvents(new List<LaneCalibrationEvent> { evt });
        }

        private void RunParking()
        {
            var batch = new List<RfidDetection>(250);
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    RfidDetection first;
                    if (!_parkingQueue.TryTake(out first, 100))
                    {
                        if (_parkingQueue.IsCompleted) break;
                        continue;
                    }
                    batch.Add(first);
                    while (batch.Count < 250 && _parkingQueue.TryTake(out first)) batch.Add(first);
                    PersistParkingWithRetry(batch);
                    batch.Clear();
                }
                catch (Exception ex)
                {
                    if (_logger != null) _logger.Error("parking-outbox", "Outbox writer failed", ex);
                    Thread.Sleep(500);
                }
            }
        }

        private void RunLaneCalibration()
        {
            var batch = new List<LaneCalibrationEvent>(100);
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    LaneCalibrationEvent first;
                    if (!_laneCalibrationQueue.TryTake(out first, 100))
                    {
                        if (_laneCalibrationQueue.IsCompleted) break;
                        continue;
                    }
                    batch.Add(first);
                    while (batch.Count < 100 && _laneCalibrationQueue.TryTake(out first)) batch.Add(first);
                    PersistLaneCalibrationWithRetry(batch);
                    batch.Clear();
                }
                catch (Exception ex)
                {
                    if (_logger != null) _logger.Error("lane-calibration-outbox", "Outbox writer failed", ex);
                    Thread.Sleep(500);
                }
            }
        }

        private void PersistParkingWithRetry(IList<RfidDetection> batch)
        {
            while (batch.Count > 0)
            {
                try
                {
                    _store.EnqueueDetections(batch);
                    return;
                }
                catch (Exception ex)
                {
                    if (_cts.IsCancellationRequested)
                    {
                        if (_logger != null) _logger.Error("parking-outbox", "Could not flush batch during shutdown", ex);
                        return;
                    }
                    if (_logger != null) _logger.Warn("parking-outbox", "Local database unavailable; retrying", ex.Message);
                    Thread.Sleep(1000);
                }
            }
        }

        private void PersistLaneCalibrationWithRetry(IList<LaneCalibrationEvent> batch)
        {
            while (batch.Count > 0)
            {
                try
                {
                    _store.EnqueueLaneCalibrationEvents(batch);
                    if (_logger != null)
                        _logger.Info(
                            "lane-calibration-outbox",
                            "Lane Calibration events persisted locally",
                            "count=" + batch.Count
                            + "; code=" + (batch[0].LaneCalibrationCode ?? string.Empty)
                            + "; durable=true");
                    return;
                }
                catch (Exception ex)
                {
                    if (_cts.IsCancellationRequested)
                    {
                        if (_logger != null) _logger.Error("lane-calibration-outbox", "Could not flush batch during shutdown", ex);
                        return;
                    }
                    if (_logger != null) _logger.Warn("lane-calibration-outbox", "Local database unavailable; retrying", ex.Message);
                    Thread.Sleep(1000);
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _parkingQueue.CompleteAdding();
            _laneCalibrationQueue.CompleteAdding();
            try
            {
                if (!_parkingThread.Join(3000) && _logger != null)
                    _logger.Warn("parking-outbox", "Parking outbox writer did not drain before timeout");
                if (!_laneCalibrationThread.Join(3000) && _logger != null)
                    _logger.Warn("lane-calibration-outbox", "Lane Calibration outbox writer did not drain before timeout");
            }
            catch (Exception ex)
            {
                if (_logger != null) _logger.Error("outbox", "Outbox writer graceful shutdown failed", ex);
            }

            _cts.Cancel();
            try
            {
                _parkingThread.Join(1000);
                _laneCalibrationThread.Join(1000);
            }
            catch (Exception ex)
            {
                if (_logger != null) _logger.Error("outbox", "Outbox writer forced shutdown failed", ex);
            }
            _parkingQueue.Dispose();
            _laneCalibrationQueue.Dispose();
            _cts.Dispose();
        }
    }
}
