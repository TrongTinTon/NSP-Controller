using System;
using System.Collections.Generic;
using System.Linq;

namespace NSPGatekeeper.Controller.Domain
{
    public sealed class ReaderDeviceConfig
    {
        public string DriverKey { get; set; }
        public string SerialNumber { get; set; }
        public string Endpoint { get; set; }
        public int Port { get; set; }
        public bool Enabled { get; set; }
        public string ConfigHash { get; set; }
        public int PowerDbm { get; set; }
        public int ReadIntervalMs { get; set; }
        public int TidStartAddress { get; set; }
        public int TidLength { get; set; }
        public IDictionary<string, string> Options { get; set; }

        public ReaderDeviceConfig()
        {
            Enabled = true;
            PowerDbm = 30;
            ReadIntervalMs = 200;
            TidStartAddress = 2;
            TidLength = 4;
            Options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public sealed class RfidDetection
    {
        public string EventUid { get; set; }
        public string SerialNumber { get; set; }
        public int PortNo { get; set; }
        public string Tid { get; set; }
        public double? RssiDbm { get; set; }
        public DateTime DetectedAtUtc { get; set; }
    }

    public sealed class ReaderDiscoveryObservation
    {
        public string DriverKey { get; set; }
        public string SerialNumber { get; set; }
        public string Endpoint { get; set; }
        public string FirmwareVersion { get; set; }
        public DateTime DiscoveredAtUtc { get; set; }
    }

    public sealed class ReaderStatus
    {
        // SerialNumber is always the physical SDK SerialNumber observed by Controller.
        public string DriverKey { get; set; }
        public string SerialNumber { get; set; }
        // Compatibility aliases used by the local observation UI/cache only.
        public string DetectedSdkSerialNumber { get; set; }
        public string DetectedEndpoint { get; set; }
        public string Model { get; set; }
        public string Endpoint { get; set; }
        public bool Online { get; set; }
        public string Message { get; set; }
        public string FirmwareVersion { get; set; }
        public int PowerDbm { get; set; }
        public int ReadIntervalMs { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
        public IList<int> Ports { get; set; }

        public ReaderStatus()
        {
            Ports = new List<int>();
        }
    }

    public sealed class OutboxItem
    {
        public long Id { get; set; }
        public RfidDetection Detection { get; set; }
        public int Attempts { get; set; }
    }

    public sealed class LaneCalibrationSessionConfig
    {
        public bool Available { get; set; }
        public string LaneCalibrationCode { get; set; }
        public string Status { get; set; }
        public string DesiredState { get; set; }
        public int Revision { get; set; }
        public IList<LaneCalibrationReaderConfig> Readers { get; set; }

        public LaneCalibrationSessionConfig()
        {
            Revision = 1;
            Readers = new List<LaneCalibrationReaderConfig>();
        }

        public bool IsRunningDesired
        {
            get { return Available && string.Equals(DesiredState, "running", StringComparison.OrdinalIgnoreCase); }
        }

        public LaneCalibrationReaderConfig Reader(string serialNumber)
        {
            var serial = (serialNumber ?? string.Empty).Trim();
            return (Readers ?? new List<LaneCalibrationReaderConfig>())
                .FirstOrDefault(value => value != null && string.Equals(value.SerialNumber, serial, StringComparison.OrdinalIgnoreCase));
        }
    }

    public sealed class LaneCalibrationReaderConfig
    {
        public string SerialNumber { get; set; }
        public int PowerDbm { get; set; }
        public int ReadIntervalMs { get; set; }

        public LaneCalibrationReaderConfig()
        {
            PowerDbm = 30;
            ReadIntervalMs = 200;
        }
    }

    public sealed class LaneCalibrationEvent
    {
        public string EventUid { get; set; }
        public string LaneCalibrationCode { get; set; }
        public int Revision { get; set; }
        public int PowerDbm { get; set; }
        public int ReadIntervalMs { get; set; }
        public string SerialNumber { get; set; }
        public int PortNo { get; set; }
        public string Tid { get; set; }
        public double? RssiDbm { get; set; }
        public DateTime ReadAtUtc { get; set; }
    }

    public sealed class LaneCalibrationOutboxItem
    {
        public long Id { get; set; }
        public LaneCalibrationEvent Event { get; set; }
        public int Attempts { get; set; }
    }
}
