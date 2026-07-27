using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using NSPGatekeeper.Controller.Domain;
using NSPGatekeeper.Controller.Infrastructure.Database;
using NSPGatekeeper.Controller.Infrastructure.Logging;

namespace NSPGatekeeper.Controller.Services
{
    /// <summary>
    /// Keeps Reader SDK callbacks free of database/network I/O.
    /// Parking and Measurement events are persisted in independent durable outboxes.
    /// </summary>
    public sealed class DetectionOutboxWriter : IDisposable
    {
        private readonly LocalStore _store;
        private readonly FileLogger _logger;
        private readonly BlockingCollection<RfidDetection> _parkingQueue = new BlockingCollection<RfidDetection>(20000);
        private readonly BlockingCollection<MeasurementEvent> _measurementQueue = new BlockingCollection<MeasurementEvent>(10000);
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly Thread _parkingThread;
        private readonly Thread _measurementThread;
        private bool _disposed;

        public DetectionOutboxWriter(LocalStore store, FileLogger logger)
        {
            _store = store ?? throw new ArgumentNullException("store");
            _logger = logger;
            _parkingThread = new Thread(RunParking) { IsBackground = true, Name = "Parking outbox writer" };
            _measurementThread = new Thread(RunMeasurement) { IsBackground = true, Name = "Measurement outbox writer" };
            _parkingThread.Start();
            _measurementThread.Start();
        }

        public void EnqueueParking(RfidDetection detection)
        {
            if (detection == null || _disposed) return;
            if (_parkingQueue.TryAdd(detection)) return;

            if (_logger != null) _logger.Warn("parking-outbox", "Ingest queue full; using synchronous persistence", "serial=" + detection.DeviceSerial);
            _store.EnqueueDetections(new List<RfidDetection> { detection });
        }

        public void EnqueueMeasurement(MeasurementEvent evt)
        {
            if (evt == null || _disposed) return;
            if (_measurementQueue.TryAdd(evt)) return;

            if (_logger != null) _logger.Warn("measurement-outbox", "Ingest queue full; using synchronous persistence", "serial=" + evt.SerialNumber);
            _store.EnqueueMeasurementEvents(new List<MeasurementEvent> { evt });
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

        private void RunMeasurement()
        {
            var batch = new List<MeasurementEvent>(100);
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    MeasurementEvent first;
                    if (!_measurementQueue.TryTake(out first, 100))
                    {
                        if (_measurementQueue.IsCompleted) break;
                        continue;
                    }
                    batch.Add(first);
                    while (batch.Count < 100 && _measurementQueue.TryTake(out first)) batch.Add(first);
                    PersistMeasurementWithRetry(batch);
                    batch.Clear();
                }
                catch (Exception ex)
                {
                    if (_logger != null) _logger.Error("measurement-outbox", "Outbox writer failed", ex);
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

        private void PersistMeasurementWithRetry(IList<MeasurementEvent> batch)
        {
            while (batch.Count > 0)
            {
                try
                {
                    _store.EnqueueMeasurementEvents(batch);
                    return;
                }
                catch (Exception ex)
                {
                    if (_cts.IsCancellationRequested)
                    {
                        if (_logger != null) _logger.Error("measurement-outbox", "Could not flush batch during shutdown", ex);
                        return;
                    }
                    if (_logger != null) _logger.Warn("measurement-outbox", "Local database unavailable; retrying", ex.Message);
                    Thread.Sleep(1000);
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _parkingQueue.CompleteAdding();
            _measurementQueue.CompleteAdding();
            try { _parkingThread.Join(3000); } catch { }
            try { _measurementThread.Join(3000); } catch { }
            try { _cts.Cancel(); } catch { }
            try { _parkingThread.Join(1000); } catch { }
            try { _measurementThread.Join(1000); } catch { }
            _parkingQueue.Dispose();
            _measurementQueue.Dispose();
            _cts.Dispose();
        }
    }
}
