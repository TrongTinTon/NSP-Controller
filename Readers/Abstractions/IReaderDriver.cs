using System;
using System.Collections.Generic;
using NSPGatekeeper.Controller.Domain;

namespace NSPGatekeeper.Controller.Readers
{
    public interface IReaderDriverFactory
    {
        string DriverKey { get; }
        IReaderRuntime Create(ReaderDeviceConfig config);
    }

    public interface IReaderDiscoveryProvider
    {
        string DriverKey { get; }
        IList<ReaderDiscoveryObservation> Discover(ISet<string> excludedEndpoints);
    }

    public interface IReaderRuntime : IDisposable
    {
        event Action<RfidDetection> DetectionReceived;
        event Action<ReaderStatus> StatusChanged;

        void Start();
        void Stop();
    }
}
