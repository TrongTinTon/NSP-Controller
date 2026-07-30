using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using NSPGatekeeper.Controller.Domain;
using NSPGatekeeper.Controller.Infrastructure.Logging;
using NSPGatekeeper.Controller.Readers;

namespace NSPGatekeeper.Controller.Readers.CFE718
{
    public sealed class Cfe718ReaderFactory : IReaderDriverFactory
    {
        private readonly FileLogger _logger;

        public Cfe718ReaderFactory(FileLogger logger)
        {
            _logger = logger;
        }

        public string DriverKey { get { return "cf-e718"; } }
        public string DisplayName { get { return "CHAFON CF-E718 / UHFReader288"; } }

        public IReaderRuntime Create(ReaderDeviceConfig config)
        {
            return new Cfe718ReaderRuntime(config, _logger);
        }
    }

    /// <summary>
    /// One instance owns exactly one physical reader.
    /// It continuously reads TID and emits every SDK callback as a raw detection.
    /// It emits raw reader data only; business processing is owned by Edge.
    /// </summary>
    internal sealed class Cfe718ReaderRuntime : IReaderRuntime
    {
        private readonly FileLogger _logger;
        private readonly object _statusGate = new object();
        private readonly object _detectionLogGate = new object();
        private readonly Dictionary<string, DateTime> _detectionLogAt = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly ReaderDeviceConfig _config;
        private Thread _thread;
        private Cfe718Native.RFIDCallBack _callback;
        private long _sequence;
        private int _currentAntennaId;
        private bool _disposed;
        private ReaderStatus _status;

        public Cfe718ReaderRuntime(ReaderDeviceConfig config, FileLogger logger)
        {
            _config = config ?? throw new ArgumentNullException("config");
            _logger = logger;
            _status = CreateStatus(false, "stopped");
        }

        public string DeviceCode { get { return _config.DeviceCode; } }
        public ReaderDeviceConfig Configuration { get { return _config; } }
        public ReaderStatus Status { get { lock (_statusGate) return CloneStatus(_status); } }

        public event Action<RfidDetection> DetectionReceived;
        public event Action<ReaderStatus> StatusChanged;

        public void Start()
        {
            ThrowIfDisposed();
            if (_thread != null && _thread.IsAlive) return;
            _thread = new Thread(Run) { IsBackground = true, Name = "RFID " + _config.DeviceCode };
            _thread.Start();
        }

        public void Stop()
        {
            try { _cts.Cancel(); } catch { }
            var thread = _thread;
            if (thread != null && thread.IsAlive && thread != Thread.CurrentThread)
            {
                try { thread.Join(3000); } catch { }
            }
        }

        private void Run()
        {
            while (!_cts.IsCancellationRequested)
            {
                int handle = -1;
                int comPort = 0;
                byte comAddress = ReadByte("comAddr", 0xFF);
                try
                {
                    SetStatus(false, "connecting");
                    var openResult = Open(ref comAddress, ref handle, ref comPort);
                    if (openResult != 0)
                        throw new InvalidOperationException("Open reader failed. SDK return=" + openResult);

                    ApplyConfiguration(ref comAddress, handle);
                    _callback = OnTagReported;
                    Cfe718Native.InitRFIDCallBack(_callback, true, handle);
                    UpdateIdentityFromReader(ref comAddress, handle);
                    SetStatus(true, "inventory_running");
                    if (_logger != null) _logger.Info("reader-cf-e718", "RFID inventory started", DescribeEndpoint());

                    var antennas = EnabledAntennas();
                    while (!_cts.IsCancellationRequested)
                    {
                        foreach (var antennaId in antennas)
                        {
                            if (_cts.IsCancellationRequested) break;
                            Interlocked.Exchange(ref _currentAntennaId, antennaId);
                            var ret = InventoryOnce(ref comAddress, handle, antennaId);
                            if (!IsAcceptedInventoryReturn(ret))
                                throw new InvalidOperationException("Inventory_G2 failed. antenna=" + antennaId + "; sdk_return=" + ret);
                            Wait(ReadInt("antennaDelayMs", 3, 0, 1000));
                        }
                        Interlocked.Exchange(ref _currentAntennaId, 0);
                        Wait(ReadInt("loopDelayMs", 1, 0, 1000));
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    SetStatus(false, ex.Message);
                    if (_logger != null) _logger.Warn("reader-cf-e718", "Reader disconnected", DescribeEndpoint() + "; " + ex.Message);
                }
                finally
                {
                    Interlocked.Exchange(ref _currentAntennaId, 0);
                    if (handle != -1)
                    {
                        try { Cfe718Native.StopInventory(ref comAddress, handle); } catch { }
                    }
                    Close(handle, comPort);
                }

                if (!_cts.IsCancellationRequested)
                    Wait(ReadInt("reconnectDelayMs", 1000, 250, 60000));
            }
            SetStatus(false, "stopped");
        }

        private void OnTagReported(IntPtr pointer, int evt)
        {
            try
            {
                var tag = (Cfe718Native.RFIDTag)Marshal.PtrToStructure(pointer, typeof(Cfe718Native.RFIDTag));
                var tid = Clean(tag.UID);
                if (string.IsNullOrWhiteSpace(tid)) return;

                // Inventory is executed one configured antenna at a time. Prefer the
                // Controller-selected antenna because RFIDTag.ANT encoding differs between
                // UHFReader288 SDK variants; fall back to the SDK value only when needed.
                // In both cases the antenna must exist in the current server configuration.
                var currentAntennaId = Thread.VolatileRead(ref _currentAntennaId);
                var reportedAntennaId = DecodeAntenna(tag.ANT);
                var antennaId = IsEnabledAntenna(currentAntennaId) ? currentAntennaId : reportedAntennaId;
                if (!IsEnabledAntenna(antennaId))
                {
                    if (_logger != null)
                        _logger.Warn("reader-cf-e718", "Dropped detection from disabled/unconfigured antenna",
                            "device=" + _config.DeviceCode + "; ant=" + antennaId.ToString(CultureInfo.InvariantCulture));
                    return;
                }

                var detection = new RfidDetection
                {
                    DeviceCode = _config.DeviceCode,
                    DeviceSerial = _status.SerialNumber ?? _config.SerialNumber,
                    AntennaId = antennaId,
                    Tid = tid.ToUpperInvariant(),
                    RssiDbm = Convert.ToDouble(tag.RSSI, CultureInfo.InvariantCulture),
                    SequenceNo = Interlocked.Increment(ref _sequence),
                    DetectedAtUtc = DateTime.UtcNow
                };


                var handler = DetectionReceived;
                if (handler != null) handler(detection);

                LogDetection(detection);
            }
            catch (Exception ex)
            {
                if (_logger != null) _logger.Warn("reader-cf-e718", "RFID callback parse failed", ex.Message);
            }
        }

        private void LogDetection(RfidDetection detection)
        {
            if (_logger == null || detection == null) return;
            var now = detection.DetectedAtUtc;
            var key = detection.Tid + "|" + detection.AntennaId.ToString(CultureInfo.InvariantCulture);
            lock (_detectionLogGate)
            {
                DateTime last;
                if (_detectionLogAt.TryGetValue(key, out last) && (now - last).TotalMilliseconds < 1000) return;
                _detectionLogAt[key] = now;
                if (_detectionLogAt.Count > 2000)
                {
                    var cutoff = now.AddMinutes(-2);
                    foreach (var oldKey in _detectionLogAt.Where(x => x.Value < cutoff).Select(x => x.Key).ToList()) _detectionLogAt.Remove(oldKey);
                }
            }

            _logger.Info("reader-detection", "Detected RFID TID",
                "device=" + _config.DeviceCode
                + "; ant=" + detection.AntennaId.ToString(CultureInfo.InvariantCulture)
                + "; tid=" + detection.Tid
                + "; rssi=" + (detection.RssiDbm.HasValue ? detection.RssiDbm.Value.ToString(CultureInfo.InvariantCulture) : ""));
        }

        private void ApplyConfiguration(ref byte comAddress, int handle)
        {
            var antennas = EnabledAntennas();
            if (antennas.Count == 0) throw new InvalidOperationException("At least one antenna must be enabled.");

            var tidAddr = ClampByte(_config.TidStartAddress, 0, 255);
            var tidLen = ClampByte(_config.TidLength, 1, 255);
            var tidRet = Cfe718Native.SetTIDParameter(ref comAddress, tidAddr, tidLen, handle);
            if (tidRet != 0 && _logger != null) _logger.Warn("reader-cf-e718", "SetTIDParameter returned warning", "ret=" + tidRet);

            var scanTimeUnits = Math.Max(1, Math.Min(255, (int)Math.Ceiling(Math.Max(1, _config.ReadIntervalMs) / 100.0)));
            var scanTime = ClampByte(scanTimeUnits, 1, 255);
            var scanRet = Cfe718Native.SetInventoryScanTime(ref comAddress, scanTime, handle);
            if (scanRet != 0 && _logger != null) _logger.Warn("reader-cf-e718", "SetInventoryScanTime returned warning", "ret=" + scanRet);

            ApplyAntennaMask(ref comAddress, handle, antennas);
            ApplyAntennaPower(ref comAddress, handle);
        }

        private void ApplyAntennaMask(ref byte comAddress, int handle, IList<int> enabledAntennas)
        {
            var max = enabledAntennas.Max();
            int ret;
            if (max <= 4)
            {
                byte mask = 0;
                foreach (var id in enabledAntennas) mask |= (byte)(1 << (id - 1));
                ret = Cfe718Native.SetAntennaMultiplexing4(ref comAddress, mask, handle);
            }
            else
            {
                byte low = 0;
                byte high = 0;
                foreach (var id in enabledAntennas.Where(x => x >= 1 && x <= 16))
                {
                    if (id <= 8) low |= (byte)(1 << (id - 1));
                    else high |= (byte)(1 << (id - 9));
                }
                ret = Cfe718Native.SetAntennaMultiplexingExtended(ref comAddress, 0x00, high, low, handle);
            }
            if (ret != 0 && _logger != null) _logger.Warn("reader-cf-e718", "Antenna mask apply returned warning", "ret=" + ret);
        }

        private void ApplyAntennaPower(ref byte comAddress, int handle)
        {
            var configured = (_config.Antennas ?? new List<ReaderAntennaConfig>()).Where(x => x.AntennaId >= 1 && x.AntennaId <= 16).ToList();
            if (configured.Count == 0) return;
            var length = Math.Max(4, configured.Max(x => x.AntennaId));
            if (length > 8) length = 16;
            else if (length > 4) length = 8;

            var powers = new byte[length];
            var power = ClampByte(_config.PowerDbm <= 0 ? 30 : _config.PowerDbm, 0, 33);
            for (var i = 0; i < powers.Length; i++) powers[i] = power;
            var ret = Cfe718Native.SetAntennaPower(ref comAddress, powers, length, handle);
            if (ret != 0 && _logger != null) _logger.Warn("reader-cf-e718", "Antenna power apply returned warning", "ret=" + ret);
        }

        private int InventoryOnce(ref byte comAddress, int handle, int antennaId)
        {
            var selector = ToAntennaSelector(antennaId, false);
            var ret = InventoryOnceWithSelector(ref comAddress, handle, selector);
            if (!IsRetryableSelectorReturn(ret)) return ret;

            var alternate = ToAntennaSelector(antennaId, true);
            if (alternate == selector) return ret;
            var alternateRet = InventoryOnceWithSelector(ref comAddress, handle, alternate);
            return IsAcceptedInventoryReturn(alternateRet) ? alternateRet : ret;
        }

        private int InventoryOnceWithSelector(ref byte comAddress, int handle, byte selector)
        {
            var maskAdr = new byte[2];
            var maskData = new byte[100];
            var epcList = new byte[50000];
            byte ant = 0;
            int totalLen = 0;
            int tagNum = 0;
            return Cfe718Native.Inventory_G2(
                ref comAddress,
                ReadByte("q", 4),
                ReadByte("session", 0),
                0x02,
                maskAdr,
                0,
                maskData,
                0,
                ClampByte(_config.TidStartAddress, 0, 255),
                ClampByte(_config.TidLength, 1, 255),
                1,
                ReadByte("target", 0),
                selector,
                ClampByte(Math.Max(1, Math.Min(255, (int)Math.Ceiling(Math.Max(1, _config.ReadIntervalMs) / 100.0))), 1, 255),
                ReadByte("fast", 1),
                epcList,
                ref ant,
                ref totalLen,
                ref tagNum,
                handle);
        }

        private int Open(ref byte comAddress, ref int handle, ref int comPort)
        {
            var endpoint = Clean(_config.Endpoint);
            var defaultConnection = string.IsNullOrWhiteSpace(endpoint) || endpoint.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ? "com" : "tcp";
            var connection = ReadString("connection", defaultConnection).ToLowerInvariant();
            if (connection == "com")
            {
                comPort = ParseComPort(endpoint);
                if (comPort > 0)
                    return Cfe718Native.OpenComPort(comPort, ref comAddress, ReadByte("baud", 6), ref handle);
                comPort = 0;
                return Cfe718Native.AutoOpenComPort(ref comPort, ref comAddress, ReadByte("baud", 6), ref handle);
            }

            if (string.IsNullOrWhiteSpace(endpoint)) throw new InvalidOperationException("Reader endpoint is required.");
            var port = _config.Port > 0 ? _config.Port : ReadInt("port", 4001, 1, 65535);
            return Cfe718Native.OpenNetPort(port, endpoint, ref comAddress, ref handle);
        }

        private static void Close(int handle, int comPort)
        {
            if (handle == -1) return;
            try { Cfe718Native.CloseNetPort(handle); } catch { }
            try { Cfe718Native.CloseUSBPort(handle); } catch { }
            try { if (comPort > 0) Cfe718Native.CloseSpecComPort(comPort); } catch { }
        }

        private void UpdateIdentityFromReader(ref byte comAddress, int handle)
        {
            string firmwareVersion = null;
            try
            {
                var module = new byte[32];
                if (Cfe718Native.GetModuleVersion(ref comAddress, module, handle) == 0)
                {
                    var value = Encoding.ASCII.GetString(module).Trim('\0', ' ', '\r', '\n');
                    if (!string.IsNullOrWhiteSpace(value)) firmwareVersion = value;
                }
            }
            catch { }

            lock (_statusGate)
            {
                // The serial configured by NSP Server is the canonical physical Reader
                // identity. GetSeriaNo() exposes a vendor/module value that may differ
                // from the chassis label, so it must not be used for identity validation.
                _status.SerialNumber = _config.SerialNumber;
                _status.Model = string.IsNullOrWhiteSpace(_config.Model) ? "CF-E718" : _config.Model;
                if (!string.IsNullOrWhiteSpace(firmwareVersion)) _status.FirmwareVersion = firmwareVersion;
            }
        }

        private bool IsEnabledAntenna(int antennaId)
        {
            if (antennaId <= 0) return false;
            return (_config.Antennas ?? new List<ReaderAntennaConfig>())
                .Any(x => x != null && x.Enabled && x.AntennaId == antennaId);
        }

        private IList<int> EnabledAntennas()
        {
            var list = (_config.Antennas ?? new List<ReaderAntennaConfig>())
                .Where(x => x.Enabled && x.AntennaId >= 1 && x.AntennaId <= 16)
                .Select(x => x.AntennaId)
                .Distinct()
                .OrderBy(x => x)
                .ToList();
            return list;
        }

        private byte ToAntennaSelector(int antennaId, bool alternate)
        {
            var mode = ReadString("antennaSelectorMode", "sequential").ToLowerInvariant();
            var sequential = (byte)(0x80 + Math.Max(0, antennaId - 1));
            var bitmask = (byte)(0x80 | (1 << Math.Max(0, antennaId - 1)));
            if (mode == "bitmask") return alternate ? sequential : bitmask;
            return alternate ? bitmask : sequential;
        }

        private static int DecodeAntenna(byte value)
        {
            if (value >= 0x80 && value <= 0x8F) return (value & 0x0F) + 1;
            if (value == 1 || value == 2 || value == 4 || value == 8) return value == 1 ? 1 : value == 2 ? 2 : value == 4 ? 3 : 4;
            return value > 0 ? value : 0;
        }

        private static bool IsAcceptedInventoryReturn(int ret)
        {
            return ret == 0 || ret == 1 || ret == 2 || ret == 0xFB || ret == 0xFF;
        }

        private static bool IsRetryableSelectorReturn(int ret)
        {
            return ret == 0xFF || ret == 0xFD;
        }

        private void SetStatus(bool online, string message)
        {
            ReaderStatus snapshot;
            lock (_statusGate)
            {
                _status.Online = online;
                _status.Message = message;
                _status.UpdatedAtUtc = DateTime.UtcNow;
                snapshot = CloneStatus(_status);
            }
            var handler = StatusChanged;
            if (handler != null) handler(snapshot);
        }

        private ReaderStatus CreateStatus(bool online, string message)
        {
            return new ReaderStatus
            {
                DeviceCode = _config.DeviceCode,
                DriverKey = "cf-e718",
                SerialNumber = _config.SerialNumber,
                Model = _config.Model,
                Endpoint = DescribeEndpoint(),
                Online = online,
                Message = message,
                ConfigRevision = _config.ConfigRevision,
                Antennas = _config.AntennaNumbers(),
                UpdatedAtUtc = DateTime.UtcNow
            };
        }

        private static ReaderStatus CloneStatus(ReaderStatus s)
        {
            return new ReaderStatus
            {
                DeviceCode = s.DeviceCode,
                DriverKey = s.DriverKey,
                SerialNumber = s.SerialNumber,
                Model = s.Model,
                Endpoint = s.Endpoint,
                Online = s.Online,
                Message = s.Message,
                FirmwareVersion = s.FirmwareVersion,
                Antennas = s.Antennas == null ? new List<int>() : new List<int>(s.Antennas),
                UpdatedAtUtc = s.UpdatedAtUtc,
                ConfigRevision = s.ConfigRevision
            };
        }

        private string DescribeEndpoint()
        {
            return (_config.Endpoint ?? string.Empty) + (_config.Port > 0 ? ":" + _config.Port : string.Empty);
        }

        private string ReadString(string key, string fallback)
        {
            string value;
            return _config.Options != null && _config.Options.TryGetValue(key, out value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : fallback;
        }

        private int ReadInt(string key, int fallback, int min, int max)
        {
            int value;
            if (!int.TryParse(ReadString(key, fallback.ToString(CultureInfo.InvariantCulture)), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) value = fallback;
            return Math.Max(min, Math.Min(max, value));
        }

        private byte ReadByte(string key, int fallback)
        {
            return ClampByte(ReadInt(key, fallback, 0, 255), 0, 255);
        }

        private static byte ClampByte(int value, int min, int max)
        {
            return (byte)Math.Max(min, Math.Min(max, value));
        }

        private static int ParseComPort(string endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint)) return 0;
            var text = endpoint.Trim();
            if (text.StartsWith("COM", StringComparison.OrdinalIgnoreCase)) text = text.Substring(3);
            int value;
            return int.TryParse(text, out value) ? value : 0;
        }

        private void Wait(int milliseconds)
        {
            if (milliseconds <= 0 || _cts.IsCancellationRequested) return;

            // Cancellation is a normal runtime shutdown/restart signal. WaitOne returns
            // immediately when Stop() cancels the token, so no exception is required to
            // wake this reader thread. The surrounding loops re-check the token before
            // performing another inventory or reconnect attempt.
            _cts.Token.WaitHandle.WaitOne(milliseconds);
        }

        private static string Clean(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(GetType().FullName);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
            // Do not dispose the CTS while a vendor SDK call may still be unwinding.
        }
    }
}
