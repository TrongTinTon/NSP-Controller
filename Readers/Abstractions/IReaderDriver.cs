using System;
using NSPGatekeeper.Controller.Domain;

namespace NSPGatekeeper.Controller.Readers
{
    /// <summary>
    /// Factory registration point. One runtime instance is created per physical reader.
    /// Adding another reader type never changes ReaderManager or the detection pipeline.
    /// </summary>
    public interface IReaderDriverFactory
    {
        string DriverKey { get; }
        string DisplayName { get; }
        IReaderRuntime Create(ReaderDeviceConfig config);
    }

    /// <summary>
    /// Runtime contract intentionally contains no parking/business methods.
    /// A reader only produces raw RFID detections and technical status.
    /// </summary>
    public interface IReaderRuntime : IDisposable
    {
        string DeviceCode { get; }
        ReaderDeviceConfig Configuration { get; }
        ReaderStatus Status { get; }

        event Action<RfidDetection> DetectionReceived;
        event Action<ReaderStatus> StatusChanged;

        void Start();
        void Stop();
    }
}
