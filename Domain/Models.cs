using System;
using System.Collections.Generic;
using System.Linq;

namespace NSPGatekeeper.Controller.Domain
{
    public sealed class ReaderAntennaConfig
    {
        public int AntennaId { get; set; }
        public bool Enabled { get; set; }
    }

    public sealed class ReaderDeviceConfig
    {
        /// <summary>
        /// Local runtime key only. Current NSP Core API does not accept device_code
        /// from Controller runtime requests; SerialNumber is the server identity.
        /// </summary>
        public string DeviceCode { get; set; }
        public string DriverKey { get; set; }
        public string DeviceName { get; set; }
        public string SerialNumber { get; set; }
        public string Model { get; set; }
        public string Endpoint { get; set; }
        public int Port { get; set; }
        public bool Enabled { get; set; }
        public int ConfigRevision { get; set; }
        public string ConfigHash { get; set; }

        public int PowerDbm { get; set; }
        public int ReadIntervalMs { get; set; }
        public int TidStartAddress { get; set; }
        public int TidLength { get; set; }

        public IList<ReaderAntennaConfig> Antennas { get; set; }
        public IDictionary<string, string> Options { get; set; }

        public ReaderDeviceConfig()
        {
            Enabled = true;
            PowerDbm = 30;
            ReadIntervalMs = 200;
            TidStartAddress = 2;
            TidLength = 4;
            Antennas = new List<ReaderAntennaConfig>();
            Options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        public IList<int> AntennaNumbers()
        {
            return (Antennas ?? new List<ReaderAntennaConfig>())
                .Where(x => x != null && x.Enabled && x.AntennaId > 0)
                .Select(x => x.AntennaId)
                .Distinct()
                .OrderBy(x => x)
                .ToList();
        }
    }

    public sealed class RfidDetection
    {
        public string EventUid { get; set; }
        public string ControllerCode { get; set; }
        public string DeviceCode { get; set; }
        public string DeviceSerial { get; set; }
        public int AntennaId { get; set; }
        public string Tid { get; set; }
        public double? RssiDbm { get; set; }
        public long SequenceNo { get; set; }
        public DateTime DetectedAtUtc { get; set; }
    }

    public sealed class ReaderStatus
    {
        public string DeviceCode { get; set; }
        public string DriverKey { get; set; }
        public string SerialNumber { get; set; }
        public string Model { get; set; }
        public string Endpoint { get; set; }
        public bool Online { get; set; }
        public string Message { get; set; }
        public string FirmwareVersion { get; set; }
        public int ConfigRevision { get; set; }
        public int PowerDbm { get; set; }
        public int ReadIntervalMs { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
        public IList<int> Antennas { get; set; }

        public ReaderStatus()
        {
            Antennas = new List<int>();
        }
    }

    public sealed class OutboxItem
    {
        public long Id { get; set; }
        public RfidDetection Detection { get; set; }
        public int Attempts { get; set; }
    }

    public sealed class MeasurementSessionConfig
    {
        public bool Available { get; set; }
        public string MeasurementCode { get; set; }
        public string ControllerCode { get; set; }
        public string Status { get; set; }
        public string DesiredState { get; set; }
        public int Revision { get; set; }
        public DateTime? PlannedStartAtUtc { get; set; }
        public DateTime? PlannedEndAtUtc { get; set; }
        public string Note { get; set; }
        public IList<MeasurementReaderConfig> Readers { get; set; }

        public MeasurementSessionConfig()
        {
            Revision = 1;
            Readers = new List<MeasurementReaderConfig>();
        }

        public bool IsRunningDesired
        {
            get { return Available && string.Equals(DesiredState, "running", StringComparison.OrdinalIgnoreCase); }
        }

        public MeasurementReaderConfig Reader(string serialNumber)
        {
            var serial = (serialNumber ?? string.Empty).Trim();
            return (Readers ?? new List<MeasurementReaderConfig>())
                .FirstOrDefault(x => x != null && string.Equals(x.SerialNumber, serial, StringComparison.OrdinalIgnoreCase));
        }

    }

    public sealed class MeasurementReaderConfig
    {
        public string SerialNumber { get; set; }
        public int PowerDbm { get; set; }
        public int ReadIntervalMs { get; set; }
        public IList<int> Antennas { get; set; }

        public MeasurementReaderConfig()
        {
            PowerDbm = 30;
            ReadIntervalMs = 200;
            Antennas = new List<int>();
        }
    }

    public sealed class MeasurementEvent
    {
        public string EventUid { get; set; }
        public string MeasurementCode { get; set; }
        public int Revision { get; set; }
        public int PowerDbm { get; set; }
        public int ReadIntervalMs { get; set; }
        public string SerialNumber { get; set; }
        public int AntennaNo { get; set; }
        public string Tid { get; set; }
        public double? RssiDbm { get; set; }
        public DateTime ReadAtUtc { get; set; }
    }

    public sealed class MeasurementOutboxItem
    {
        public long Id { get; set; }
        public MeasurementEvent Event { get; set; }
        public int Attempts { get; set; }
    }

    public sealed class CoreApiAuthResult
    {
        public bool Success { get; set; }
        public string AccessToken { get; set; }
        public DateTime? ExpiresAtUtc { get; set; }
        public string Message { get; set; }
    }
}
