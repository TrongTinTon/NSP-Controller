using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO.Ports;
using System.Reflection;
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
        public IReaderRuntime Create(ReaderDeviceConfig config)
        {
            return new Cfe718ReaderRuntime(config, _logger);
        }
    }

    internal sealed class Cfe718ReaderRuntime : IReaderRuntime
    {
        private readonly FileLogger _logger;
        private static readonly object ComDiscoveryGate = new object();
        private static readonly int[] HardwarePorts = { 1, 2, 3, 4 };
        private readonly object _statusGate = new object();
        private readonly object _detectionLogGate = new object();
        private readonly Dictionary<string, DateTime> _detectionLogAt = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly ReaderDeviceConfig _config;
        private Thread _thread;
        private Cfe718Native.RFIDCallBack _callback;
        private int _currentPortNo;
        private bool _disposed;
        private ReaderStatus _status;
        private string _resolvedEndpoint;
        private string _hardwareSerialNumber;
        private string _lastFailureSignature;
        private DateTime _lastFullFailureAtUtc;
        private string _lastPortFailureSignature;
        private DateTime _lastPortFailureLogAtUtc;

        private enum ConnectionKind
        {
            None,
            Com,
            Tcp
        }

        public Cfe718ReaderRuntime(ReaderDeviceConfig config, FileLogger logger)
        {
            _config = config ?? throw new ArgumentNullException("config");
            _logger = logger;
            _status = CreateStatus(false, "stopped");
        }

        public event Action<RfidDetection> DetectionReceived;
        public event Action<ReaderStatus> StatusChanged;

        public void Start()
        {
            ThrowIfDisposed();
            if (_thread != null && _thread.IsAlive) return;

            if (_logger != null)
                _logger.Info(
                    "reader-runtime",
                    "Reader worker starting",
                    ReaderConfigDescription() + "; sdk=" + Cfe718Native.DescribeRuntime());

            _thread = new Thread(Run) { IsBackground = true, Name = "RFID " + _config.SerialNumber };
            _thread.Start();
        }

        public void Stop()
        {
            _cts.Cancel();
            var thread = _thread;
            if (thread == null || !thread.IsAlive || thread == Thread.CurrentThread) return;

            try
            {
                var timeout = ReadInt("shutdownTimeoutMs", 10000, 1000, 60000);
                if (!thread.Join(timeout) && _logger != null)
                    _logger.Warn(
                        "reader-runtime",
                        "Reader worker did not stop before timeout",
                        "serial=" + _config.SerialNumber + "; endpoint=" + DescribeEndpoint() + "; timeout_ms=" + timeout);
            }
            catch (Exception ex)
            {
                if (_logger != null)
                    _logger.Error("reader-runtime", "Reader worker stop failed", ex, ReaderConfigDescription());
            }
        }

        private void Run()
        {
            var consecutiveFailures = 0;
            while (!_cts.IsCancellationRequested)
            {
                var phase = "prepare";
                int handle = -1;
                int comPort = 0;
                var connectionKind = ConnectionKind.None;
                byte comAddress = ReadByte("comAddr", 0xFF);

                try
                {
                    _hardwareSerialNumber = null;
                    _resolvedEndpoint = null;
                    var attempt = consecutiveFailures + 1;
                    SetStatus(false, consecutiveFailures == 0
                        ? "connecting"
                        : "reconnecting_attempt_" + attempt.ToString(CultureInfo.InvariantCulture));

                    if (_logger != null)
                        _logger.Info(
                            "reader-connect",
                            "Reader connection attempt started",
                            "attempt=" + attempt.ToString(CultureInfo.InvariantCulture)
                            + "; " + ConnectionAttemptDescription(comAddress));

                    phase = "open_transport";
                    var openResult = Open(ref comAddress, ref handle, ref comPort, out connectionKind);
                    EnsureSdkSuccess(
                        "Open " + connectionKind,
                        openResult,
                        "handle=" + handle
                        + "; com_port=" + comPort
                        + "; com_address=" + FormatByte(comAddress)
                        + "; " + ReaderConfigDescription());

                    if (_logger != null)
                        _logger.Info(
                            "reader-connect",
                            "Reader transport opened",
                            "connection=" + connectionKind
                            + "; endpoint=" + DescribeEndpoint()
                            + "; handle=" + handle
                            + "; com_port=" + comPort
                            + "; com_address=" + FormatByte(comAddress));

                    phase = "read_sdk_identity";
                    if (string.IsNullOrWhiteSpace(_hardwareSerialNumber))
                        UpdateIdentityFromReader(ref comAddress, handle);

                    phase = "apply_reader_configuration";
                    ApplyConfiguration(ref comAddress, handle);

                    phase = "register_callback";
                    _callback = OnTagReported;
                    Cfe718Native.InitRFIDCallBack(_callback, true, handle);
                    if (_logger != null)
                        _logger.Info(
                            "reader-connect",
                            "RFID callback registered",
                            "serial=" + (_hardwareSerialNumber ?? _config.SerialNumber)
                            + "; endpoint=" + DescribeEndpoint()
                            + "; handle=" + handle);

                    consecutiveFailures = 0;
                    _lastFailureSignature = null;
                    SetStatus(true, "inventory_running");
                    if (_logger != null)
                        _logger.Info(
                            "reader-inventory",
                            "RFID inventory started",
                            ReaderConfigDescription() + "; endpoint=" + DescribeEndpoint());

                    var ports = ReaderPorts();
                    while (!_cts.IsCancellationRequested)
                    {
                        var acceptedPorts = 0;
                        var failures = new List<string>();
                        foreach (var portNo in ports)
                        {
                            if (_cts.IsCancellationRequested) break;
                            phase = "inventory_port_" + portNo.ToString(CultureInfo.InvariantCulture);
                            Interlocked.Exchange(ref _currentPortNo, portNo);
                            var result = InventoryOnce(ref comAddress, handle, portNo);
                            if (IsAcceptedInventoryReturn(result))
                            {
                                acceptedPorts++;
                            }
                            else
                            {
                                failures.Add("port=" + portNo + ":" + Cfe718Native.FormatResult(result));
                            }
                            Wait(ReadInt("portDelayMs", 3, 0, 1000));
                        }

                        if (!_cts.IsCancellationRequested && acceptedPorts == 0)
                        {
                            throw new InvalidOperationException(
                                "Inventory failed on every Reader Port. " + string.Join(" | ", failures));
                        }

                        if (failures.Count > 0)
                            LogPartialPortFailures(failures, acceptedPorts);

                        Interlocked.Exchange(ref _currentPortNo, 0);
                        Wait(ReadInt("loopDelayMs", 1, 0, 1000));
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    consecutiveFailures++;
                    var root = Unwrap(ex);
                    var context = "phase=" + phase
                        + "; diagnosis=" + DiagnoseFailure(phase, root)
                        + "; attempt=" + consecutiveFailures.ToString(CultureInfo.InvariantCulture)
                        + "; connection=" + connectionKind
                        + "; endpoint=" + DescribeEndpoint()
                        + "; handle=" + handle
                        + "; com_port=" + comPort
                        + "; com_address=" + FormatByte(comAddress)
                        + "; windows_com=" + WindowsComPorts()
                        + "; " + ReaderConfigDescription()
                        + "; sdk=" + Cfe718Native.DescribeRuntime();

                    SetStatus(false, "reconnecting: " + root.Message);
                    LogConnectionFailure(root, context);
                }
                finally
                {
                    Interlocked.Exchange(ref _currentPortNo, 0);
                    if (handle != -1)
                    {
                        try
                        {
                            var stopResult = Cfe718Native.StopInventory(ref comAddress, handle);
                            if (stopResult != 0 && _logger != null)
                                _logger.Warn(
                                    "reader-disconnect",
                                    "StopInventory returned a non-zero SDK result",
                                    "result=" + Cfe718Native.FormatResult(stopResult)
                                    + "; endpoint=" + DescribeEndpoint()
                                    + "; handle=" + handle);
                        }
                        catch (Exception stopError)
                        {
                            if (_logger != null)
                                _logger.Error(
                                    "reader-disconnect",
                                    "StopInventory threw an exception",
                                    Unwrap(stopError),
                                    "endpoint=" + DescribeEndpoint() + "; handle=" + handle);
                        }
                    }

                    Close(handle, comPort, connectionKind);
                }

                if (!_cts.IsCancellationRequested)
                {
                    var delay = ReconnectDelay(consecutiveFailures);
                    if (_logger != null)
                        _logger.Info(
                            "reader-reconnect",
                            "Waiting before automatic Reader reconnect",
                            "serial=" + _config.SerialNumber
                            + "; endpoint=" + DescribeEndpoint()
                            + "; next_attempt=" + (consecutiveFailures + 1).ToString(CultureInfo.InvariantCulture)
                            + "; delay_ms=" + delay.ToString(CultureInfo.InvariantCulture));
                    Wait(delay);
                }
            }

            SetStatus(false, "stopped");
            if (_logger != null)
                _logger.Info("reader-runtime", "Reader worker stopped", ReaderConfigDescription());
        }

        private void OnTagReported(IntPtr pointer, int evt)
        {
            try
            {
                var tag = (Cfe718Native.RFIDTag)Marshal.PtrToStructure(pointer, typeof(Cfe718Native.RFIDTag));
                var tid = Clean(tag.UID);
                if (string.IsNullOrWhiteSpace(tid)) return;

                var currentPortNo = Thread.VolatileRead(ref _currentPortNo);
                var reportedPortNo = DecodeReportedPort(tag.ANT);
                var portNo = IsReaderPort(currentPortNo) ? currentPortNo : reportedPortNo;
                if (!IsReaderPort(portNo))
                {
                    if (_logger != null)
                        _logger.Warn(
                            "reader-callback",
                            "Dropped RFID detection because SDK did not report a valid port_no",
                            "serial=" + _config.SerialNumber
                            + "; sdk_ant=" + tag.ANT.ToString(CultureInfo.InvariantCulture));
                    return;
                }

                var detection = new RfidDetection
                {
                    SerialNumber = _hardwareSerialNumber ?? _status.SerialNumber ?? _config.SerialNumber,
                    PortNo = portNo,
                    Tid = tid.ToUpperInvariant(),
                    RssiDbm = Convert.ToDouble(tag.RSSI, CultureInfo.InvariantCulture),
                    DetectedAtUtc = DateTime.UtcNow
                };


                var handler = DetectionReceived;
                if (handler != null) handler(detection);

                LogDetection(detection);
            }
            catch (Exception ex)
            {
                if (_logger != null) _logger.Error("reader-callback", "RFID callback parse failed", ex, "serial=" + _config.SerialNumber + "; endpoint=" + DescribeEndpoint());
            }
        }

        private void LogDetection(RfidDetection detection)
        {
            if (_logger == null || detection == null) return;
            var now = detection.DetectedAtUtc;
            var key = detection.Tid + "|" + detection.PortNo.ToString(CultureInfo.InvariantCulture);
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
                "serial=" + detection.SerialNumber
                + "; port=" + detection.PortNo.ToString(CultureInfo.InvariantCulture)
                + "; tid=" + detection.Tid
                + "; rssi=" + (detection.RssiDbm.HasValue ? detection.RssiDbm.Value.ToString(CultureInfo.InvariantCulture) : ""));
        }

        private void LogPartialPortFailures(IList<string> failures, int acceptedPorts)
        {
            if (_logger == null || failures == null || failures.Count == 0) return;
            var signature = string.Join("|", failures);
            var now = DateTime.UtcNow;
            if (string.Equals(signature, _lastPortFailureSignature, StringComparison.Ordinal)
                && (now - _lastPortFailureLogAtUtc).TotalSeconds < 60)
            {
                return;
            }

            _lastPortFailureSignature = signature;
            _lastPortFailureLogAtUtc = now;
            _logger.Warn(
                "reader-inventory",
                "One or more Reader hardware ports returned SDK errors; remaining ports continue",
                "serial=" + (_hardwareSerialNumber ?? _config.SerialNumber)
                + "; endpoint=" + DescribeEndpoint()
                + "; accepted_ports=" + acceptedPorts
                + "; failures=" + signature
                + "; port_filtering=server");
        }

        private void ApplyConfiguration(ref byte comAddress, int handle)
        {
            var ports = ReaderPorts();

            var tidAddress = ClampByte(_config.TidStartAddress, 0, 255);
            var tidLength = ClampByte(_config.TidLength, 1, 255);
            EnsureSdkSuccess(
                "SetTIDParameter",
                Cfe718Native.SetTIDParameter(ref comAddress, tidAddress, tidLength, handle),
                "tid_start=" + tidAddress + "; tid_length=" + tidLength + "; handle=" + handle);

            var scanTimeUnits = Math.Max(1, Math.Min(255, (int)Math.Ceiling(Math.Max(1, _config.ReadIntervalMs) / 100.0)));
            var scanTime = ClampByte(scanTimeUnits, 1, 255);
            EnsureSdkSuccess(
                "SetInventoryScanTime",
                Cfe718Native.SetInventoryScanTime(ref comAddress, scanTime, handle),
                "requested_interval_ms=" + _config.ReadIntervalMs + "; sdk_scan_time=" + scanTime + "; handle=" + handle);

            ApplyPortMask(ref comAddress, handle);
            ApplyPortPower(ref comAddress, handle);

            if (_logger != null)
                _logger.Info(
                    "reader-config-apply",
                    "Reader configuration applied",
                    ReaderConfigDescription()
                    + "; endpoint=" + DescribeEndpoint()
                    + "; handle=" + handle
                    + "; com_address=" + FormatByte(comAddress));
        }

        private void ApplyPortMask(ref byte comAddress, int handle)
        {
            const byte allFourPortsMask = 0x0F;
            EnsureSdkSuccess(
                "SetAntennaMultiplexing",
                Cfe718Native.SetAntennaMultiplexing4(ref comAddress, allFourPortsMask, handle),
                "scan_ports=1,2,3,4; handle=" + handle);
        }

        private void ApplyPortPower(ref byte comAddress, int handle)
        {
            var powers = new byte[HardwarePorts.Length];
            var power = ClampByte(_config.PowerDbm, 0, 33);
            for (var i = 0; i < powers.Length; i++) powers[i] = power;
            var ret = Cfe718Native.SetAntennaPower(ref comAddress, powers, powers.Length, handle);
            EnsureSdkSuccess(
                "SetAntennaPower",
                ret,
                "power_dbm=" + power + "; array_length=" + powers.Length + "; handle=" + handle);
        }

        private int InventoryOnce(ref byte comAddress, int handle, int portNo)
        {
            var selector = ToPortSelector(portNo, false);
            var ret = InventoryOnceWithSelector(ref comAddress, handle, selector);
            if (!IsRetryableSelectorReturn(ret)) return ret;

            var alternate = ToPortSelector(portNo, true);
            if (alternate == selector) return ret;
            var alternateRet = InventoryOnceWithSelector(ref comAddress, handle, alternate);
            return alternateRet;
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

        private int Open(ref byte comAddress, ref int handle, ref int comPort, out ConnectionKind connectionKind)
        {
            connectionKind = ConnectionKind.None;
            var endpoint = Clean(_config.Endpoint);
            var defaultConnection = string.IsNullOrWhiteSpace(endpoint) || endpoint.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ? "com" : "tcp";
            var connection = ReadString("connection", defaultConnection).Trim().ToLowerInvariant();

            if (connection == "com")
            {
                return OpenMatchingComReader(ref comAddress, ref handle, ref comPort, out connectionKind);
            }

            if (connection != "tcp")
                throw new InvalidOperationException("Unsupported Reader connection type: " + connection);
            if (string.IsNullOrWhiteSpace(endpoint))
                throw new InvalidOperationException("Reader TCP endpoint is required.");

            var port = _config.Port > 0 ? _config.Port : ReadInt("port", 4001, 1, 65535);
            var networkResult = Cfe718Native.OpenNetPort(port, endpoint, ref comAddress, ref handle);
            if (networkResult == 0)
            {
                connectionKind = ConnectionKind.Tcp;
                _resolvedEndpoint = endpoint + ":" + port;
            }
            return networkResult;
        }

        private int OpenMatchingComReader(ref byte comAddress, ref int handle, ref int comPort, out ConnectionKind connectionKind)
        {
            connectionKind = ConnectionKind.None;
            var configuredEndpoint = Clean(_config.Endpoint);
            var configuredPort = ParseComPort(configuredEndpoint);
            if (!string.IsNullOrWhiteSpace(configuredEndpoint) && configuredPort <= 0)
                throw new InvalidOperationException("Invalid COM endpoint. Expected COM<number>; configured=" + configuredEndpoint);

            var candidates = WindowsComPortNumbers(configuredPort);
            if (candidates.Count == 0)
                throw new InvalidOperationException(
                    "No Windows COM ports are currently available for Reader discovery. configured="
                    + (string.IsNullOrWhiteSpace(configuredEndpoint) ? "AUTO-COM" : configuredEndpoint)
                    + "; expected_serial=" + (_config.SerialNumber ?? string.Empty));

            var baud = ReadByte("baud", 6);
            var failures = new List<string>();

            lock (ComDiscoveryGate)
            {
                foreach (var candidatePort in candidates)
                {
                    if (_cts.IsCancellationRequested) throw new OperationCanceledException();

                    var candidateAddress = ReadByte("comAddr", 0xFF);
                    var candidateHandle = -1;
                    _resolvedEndpoint = "COM" + candidatePort.ToString(CultureInfo.InvariantCulture);
                    _hardwareSerialNumber = null;

                    if (_logger != null)
                        _logger.Info(
                            "reader-discovery",
                            "Testing Windows COM port for configured Reader",
                            "expected_serial=" + (_config.SerialNumber ?? string.Empty)
                            + "; candidate=COM" + candidatePort.ToString(CultureInfo.InvariantCulture)
                            + "; configured=" + (string.IsNullOrWhiteSpace(configuredEndpoint) ? "AUTO-COM" : configuredEndpoint)
                            + "; baud_code=" + baud.ToString(CultureInfo.InvariantCulture));

                    try
                    {
                        var result = Cfe718Native.OpenComPort(candidatePort, ref candidateAddress, baud, ref candidateHandle);
                        if (result != 0)
                        {
                            failures.Add("COM" + candidatePort.ToString(CultureInfo.InvariantCulture)
                                         + ":open=" + Cfe718Native.FormatResult(result));
                            continue;
                        }

                        UpdateIdentityFromReader(ref candidateAddress, candidateHandle);

                        comAddress = candidateAddress;
                        handle = candidateHandle;
                        comPort = candidatePort;
                        connectionKind = ConnectionKind.Com;

                        if (_logger != null)
                        {
                            var changed = configuredPort > 0 && configuredPort != candidatePort;
                            _logger.Info(
                                "reader-discovery",
                                changed ? "Reader COM binding changed and will be persisted locally" : "Reader COM binding verified",
                                "serial=" + (_hardwareSerialNumber ?? _config.SerialNumber)
                                + "; previous=" + (configuredPort > 0 ? "COM" + configuredPort.ToString(CultureInfo.InvariantCulture) : "AUTO-COM")
                                + "; current=COM" + candidatePort.ToString(CultureInfo.InvariantCulture)
                                + "; windows_com=" + WindowsComPorts());
                        }

                        return 0;
                    }
                    catch (Exception ex)
                    {
                        var root = Unwrap(ex);
                        failures.Add("COM" + candidatePort.ToString(CultureInfo.InvariantCulture)
                                     + ":" + root.GetType().Name + ":" + root.Message);
                    }
                    finally
                    {
                        if (connectionKind != ConnectionKind.Com && candidateHandle != -1)
                        {
                            try
                            {
                                Cfe718Native.CloseComPort(candidatePort, candidateHandle);
                            }
                            catch (Exception closeError)
                            {
                                if (_logger != null)
                                    _logger.Error(
                                        "reader-discovery",
                                        "Could not close rejected COM candidate",
                                        Unwrap(closeError),
                                        "candidate=COM" + candidatePort.ToString(CultureInfo.InvariantCulture)
                                        + "; handle=" + candidateHandle);
                            }
                        }
                    }
                }
            }

            _resolvedEndpoint = null;
            _hardwareSerialNumber = null;
            throw new InvalidOperationException(
                "Configured Reader was not found on any current Windows COM port. expected_serial="
                + (_config.SerialNumber ?? string.Empty)
                + "; configured=" + (string.IsNullOrWhiteSpace(configuredEndpoint) ? "AUTO-COM" : configuredEndpoint)
                + "; tested=" + string.Join(" | ", failures));
        }

        private static IList<int> WindowsComPortNumbers(int preferredPort)
        {
            var ports = new List<int>();
            try
            {
                ports = SerialPort.GetPortNames()
                    .Select(ParseComPort)
                    .Where(value => value > 0)
                    .Distinct()
                    .OrderBy(value => value)
                    .ToList();
            }
            catch
            {
                ports = new List<int>();
            }

            if (preferredPort > 0 && ports.Remove(preferredPort))
                ports.Insert(0, preferredPort);

            return ports;
        }

        private void Close(int handle, int comPort, ConnectionKind connectionKind)
        {
            if (connectionKind == ConnectionKind.None) return;

            try
            {
                int result;
                if (connectionKind == ConnectionKind.Com)
                {
                    result = Cfe718Native.CloseComPort(comPort, handle);
                }
                else
                {
                    result = Cfe718Native.CloseNetPort(handle);
                }

                if (_logger != null)
                {
                    var detail = "connection=" + connectionKind
                        + "; endpoint=" + DescribeEndpoint()
                        + "; handle=" + handle
                        + "; com_port=" + comPort
                        + "; result=" + Cfe718Native.FormatResult(result);
                    if (result == 0)
                        _logger.Info("reader-disconnect", "Reader transport closed", detail);
                    else
                        _logger.Warn("reader-disconnect", "Reader transport close returned a non-zero SDK result", detail);
                }
            }
            catch (Exception ex)
            {
                if (_logger != null)
                    _logger.Error(
                        "reader-disconnect",
                        "Reader transport close failed",
                        Unwrap(ex),
                        "connection=" + connectionKind
                        + "; endpoint=" + DescribeEndpoint()
                        + "; handle=" + handle
                        + "; com_port=" + comPort);
            }
        }

        private void UpdateIdentityFromReader(ref byte comAddress, int handle)
        {
            var serialBytes = new byte[4];
            var serialResult = Cfe718Native.GetSeriaNo(ref comAddress, serialBytes, handle);
            var rawSerial = BitConverter.ToString(serialBytes).Replace("-", string.Empty);
            EnsureSdkSuccess(
                "GetSeriaNo",
                serialResult,
                "raw_serial=" + rawSerial
                + "; expected=" + (_config.SerialNumber ?? string.Empty)
                + "; endpoint=" + DescribeEndpoint()
                + "; handle=" + handle
                + "; com_address=" + FormatByte(comAddress));

            var hardwareSerial = ToHardwareSerial(serialBytes);
            if (!string.IsNullOrWhiteSpace(hardwareSerial))
            {
                lock (_statusGate)
                {
                    _status.DetectedSdkSerialNumber = hardwareSerial;
                    _status.DetectedEndpoint = DescribeEndpoint();
                    _status.UpdatedAtUtc = DateTime.UtcNow;
                }
            }

            if (string.IsNullOrWhiteSpace(hardwareSerial))
                throw new InvalidOperationException(
                    "Reader SDK returned an empty SerialNumber. raw_serial=" + rawSerial
                    + "; endpoint=" + DescribeEndpoint());

            var expectedSerial = NormalizeHardwareSerial(_config.SerialNumber);
            if (!IsHardwareSerial(expectedSerial))
                throw new InvalidOperationException(
                    "Configured Reader serial_number must be the 4-byte SDK SerialNumber encoded as 8 uppercase hexadecimal characters. configured="
                    + (_config.SerialNumber ?? string.Empty));

            if (!string.Equals(expectedSerial, hardwareSerial, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Reader SerialNumber mismatch. configured=" + expectedSerial
                    + "; sdk=" + hardwareSerial
                    + "; raw_serial=" + rawSerial
                    + "; endpoint=" + DescribeEndpoint());

            string firmwareVersion = null;
            try
            {
                var module = new byte[32];
                var versionResult = Cfe718Native.GetModuleVersion(ref comAddress, module, handle);
                if (versionResult == 0)
                {
                    var value = Encoding.ASCII.GetString(module).Trim('\0', ' ', '\r', '\n');
                    if (!string.IsNullOrWhiteSpace(value)) firmwareVersion = value;
                }
                else if (_logger != null)
                {
                    _logger.Warn(
                        "reader-identity",
                        "GetModuleVersion returned a non-zero SDK result",
                        "result=" + Cfe718Native.FormatResult(versionResult)
                        + "; serial=" + hardwareSerial
                        + "; endpoint=" + DescribeEndpoint());
                }
            }
            catch (Exception versionError)
            {
                if (_logger != null)
                    _logger.Error(
                        "reader-identity",
                        "Could not read Reader module version",
                        Unwrap(versionError),
                        "serial=" + hardwareSerial + "; endpoint=" + DescribeEndpoint());
            }

            _hardwareSerialNumber = hardwareSerial;
            if (_logger != null)
                _logger.Info(
                    "reader-identity",
                    "Reader SDK SerialNumber verified",
                    "configured=" + expectedSerial
                    + "; sdk=" + hardwareSerial
                    + "; raw_serial=" + rawSerial
                    + "; firmware=" + (firmwareVersion ?? "<unknown>")
                    + "; endpoint=" + DescribeEndpoint());

            lock (_statusGate)
            {
                _status.DetectedSdkSerialNumber = hardwareSerial;
                _status.DetectedEndpoint = DescribeEndpoint();
                _status.Model = "CF-E718";
                if (!string.IsNullOrWhiteSpace(firmwareVersion)) _status.FirmwareVersion = firmwareVersion;
            }
        }

        private static string ToHardwareSerial(byte[] value)
        {
            if (value == null || value.Length < 4) return null;
            if (value.Take(4).All(item => item == 0x00) || value.Take(4).All(item => item == 0xFF)) return null;
            return string.Concat(value.Take(4).Select(item => item.ToString("X2", CultureInfo.InvariantCulture)));
        }

        private static string NormalizeHardwareSerial(string value)
        {
            var text = (value ?? string.Empty).Trim().ToUpperInvariant();
            if (text.StartsWith("0X", StringComparison.Ordinal)) text = text.Substring(2);
            return new string(text.Where(Uri.IsHexDigit).ToArray());
        }

        private static bool IsHardwareSerial(string value)
        {
            return !string.IsNullOrWhiteSpace(value)
                && value.Length == 8
                && value.All(Uri.IsHexDigit);
        }

        private void EnsureSdkSuccess(string operation, int result, string context)
        {
            if (result == 0) return;
            throw SdkFailure(operation, result, context);
        }

        private static Exception SdkFailure(string operation, int result, string context)
        {
            return new InvalidOperationException(
                operation + " failed. sdk_result=" + Cfe718Native.FormatResult(result)
                + (string.IsNullOrWhiteSpace(context) ? string.Empty : "; " + context));
        }

        private static string DiagnoseFailure(string phase, Exception ex)
        {
            var message = ex == null ? string.Empty : ex.Message ?? string.Empty;
            if (ex is DllNotFoundException)
                return "sdk_dll_missing_or_not_in_output; verify UHFReader288.dll beside the executable";
            if (ex is BadImageFormatException)
                return "sdk_architecture_mismatch; run x86 build with x86 SDK or x64 build with x64 SDK";
            if (ex is TypeLoadException || ex is MissingMethodException)
                return "sdk_api_mismatch; deployed UHFReader288.dll does not match the Controller wrapper";
            if (message.IndexOf("SerialNumber mismatch", StringComparison.OrdinalIgnoreCase) >= 0)
                return "wrong_reader_on_endpoint; bind the configured SDK serial to the correct COM port";
            if (message.IndexOf("must be the 4-byte SDK SerialNumber", StringComparison.OrdinalIgnoreCase) >= 0)
                return "invalid_edge_reader_identity; configure the 8-character SDK hardware serial";
            if (message.IndexOf("not currently reported by Windows", StringComparison.OrdinalIgnoreCase) >= 0)
                return "configured_com_not_detected_by_windows";
            if (string.Equals(phase, "open_transport", StringComparison.OrdinalIgnoreCase))
                return "transport_open_failed; check Windows COM presence, another process holding the port, driver, baud and SDK architecture";
            if (string.Equals(phase, "read_sdk_identity", StringComparison.OrdinalIgnoreCase))
                return "transport_opened_but_reader_identity_failed; check cable, power, baud, SDK compatibility and COM mapping";
            if (string.Equals(phase, "apply_reader_configuration", StringComparison.OrdinalIgnoreCase))
                return "reader_connected_but_rejected_runtime_settings; inspect the failing SDK operation and requested power/ports/TID settings";
            if (string.Equals(phase, "register_callback", StringComparison.OrdinalIgnoreCase))
                return "reader_connected_but_callback_registration_failed; verify SDK version and callback type";
            if (!string.IsNullOrWhiteSpace(phase) && phase.StartsWith("inventory_port_", StringComparison.OrdinalIgnoreCase))
                return "inventory_failed_after_connect; Reader may have disconnected or SDK returned a runtime error";
            return "unexpected_reader_runtime_failure; inspect exception_type, hresult and stack";
        }

        private void LogConnectionFailure(Exception ex, string context)
        {
            if (_logger == null) return;

            var signature = ex.GetType().FullName + "|" + ex.Message;
            var now = DateTime.UtcNow;
            var full = !string.Equals(signature, _lastFailureSignature, StringComparison.Ordinal)
                || (now - _lastFullFailureAtUtc).TotalMinutes >= 5;

            _lastFailureSignature = signature;
            if (full)
            {
                _lastFullFailureAtUtc = now;
                _logger.Error(
                    "reader-connect",
                    "Reader connection/inventory attempt failed; automatic reconnect scheduled",
                    ex,
                    context);
            }
            else
            {
                _logger.Warn(
                    "reader-connect",
                    "Reader reconnect attempt failed with the same error",
                    context
                    + "; exception_type=" + ex.GetType().FullName
                    + "; message=" + ex.Message);
            }
        }

        private string ConnectionAttemptDescription(byte comAddress)
        {
            return ReaderConfigDescription()
                + "; requested_endpoint=" + (Clean(_config.Endpoint) ?? "AUTO-COM")
                + "; com_address=" + FormatByte(comAddress)
                + "; windows_com=" + WindowsComPorts()
                + "; sdk=" + Cfe718Native.DescribeRuntime();
        }

        private string ReaderConfigDescription()
        {
            return "configured_serial=" + (_config.SerialNumber ?? "<empty>")
                + "; driver=" + (_config.DriverKey ?? "<empty>")
                + "; connection=" + ReadString("connection", string.IsNullOrWhiteSpace(_config.Endpoint) || (_config.Endpoint ?? string.Empty).StartsWith("COM", StringComparison.OrdinalIgnoreCase) ? "com" : "tcp")
                + "; endpoint=" + (string.IsNullOrWhiteSpace(_config.Endpoint) ? "AUTO-COM" : _config.Endpoint)
                + "; tcp_port=" + _config.Port
                + "; scan_ports=" + string.Join(",", ReaderPorts())
                + "; port_filtering=server"
                + "; power_dbm=" + _config.PowerDbm
                + "; read_interval_ms=" + _config.ReadIntervalMs
                + "; tid_start=" + _config.TidStartAddress
                + "; tid_length=" + _config.TidLength
                + "; config_hash=" + (_config.ConfigHash ?? "<empty>");
        }

        private static string WindowsComPorts()
        {
            try
            {
                var ports = SerialPort.GetPortNames()
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value.Trim().ToUpperInvariant())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                return ports.Length == 0 ? "none" : string.Join(",", ports);
            }
            catch (Exception ex)
            {
                return "enumeration_failed:" + ex.GetType().Name + ":" + ex.Message;
            }
        }


        private static string FormatByte(byte value)
        {
            return value.ToString(CultureInfo.InvariantCulture) + " (0x" + value.ToString("X2", CultureInfo.InvariantCulture) + ")";
        }

        private static Exception Unwrap(Exception ex)
        {
            var current = ex;
            while (current is TargetInvocationException && current.InnerException != null)
                current = current.InnerException;
            return current ?? ex;
        }

        private static bool IsReaderPort(int portNo)
        {
            return portNo >= 1 && portNo <= 16;
        }

        private static IList<int> ReaderPorts()
        {
            return new List<int>(HardwarePorts);
        }

        private byte ToPortSelector(int portNo, bool alternate)
        {
            var mode = ReadString("portSelectorMode", "sequential").ToLowerInvariant();
            var sequential = (byte)(0x80 + Math.Max(0, portNo - 1));
            var bitmask = (byte)(0x80 | (1 << Math.Max(0, portNo - 1)));
            if (mode == "bitmask") return alternate ? sequential : bitmask;
            return alternate ? bitmask : sequential;
        }

        private static int DecodeReportedPort(byte value)
        {
            if (value >= 0x80 && value <= 0x8F) return (value & 0x0F) + 1;
            if (value == 1 || value == 2 || value == 4 || value == 8) return value == 1 ? 1 : value == 2 ? 2 : value == 4 ? 3 : 4;
            return value > 0 ? value : 0;
        }

        private static bool IsAcceptedInventoryReturn(int ret)
        {
            // 0xFB means no Tag was found and is a normal inventory result.
            // 0xFF/0xFD are not accepted: keeping them as success prevents the
            // runtime from detecting a broken connection and reconnecting.
            return ret == 0 || ret == 1 || ret == 2 || ret == 0xFB;
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
                _status.Endpoint = DescribeEndpoint();
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
                DriverKey = "cf-e718",
                SerialNumber = _config.SerialNumber,
                DetectedSdkSerialNumber = null,
                DetectedEndpoint = null,
                Model = "CF-E718",
                Endpoint = DescribeEndpoint(),
                Online = online,
                Message = message,
                PowerDbm = Math.Max(0, Math.Min(33, _config.PowerDbm)),
                ReadIntervalMs = Math.Max(1, Math.Min(60000, _config.ReadIntervalMs)),
                Ports = ReaderPorts(),
                UpdatedAtUtc = DateTime.UtcNow
            };
        }

        private static ReaderStatus CloneStatus(ReaderStatus s)
        {
            return new ReaderStatus
            {
                DriverKey = s.DriverKey,
                SerialNumber = s.SerialNumber,
                DetectedSdkSerialNumber = s.DetectedSdkSerialNumber,
                DetectedEndpoint = s.DetectedEndpoint,
                Model = s.Model,
                Endpoint = s.Endpoint,
                Online = s.Online,
                Message = s.Message,
                FirmwareVersion = s.FirmwareVersion,
                PowerDbm = s.PowerDbm,
                ReadIntervalMs = s.ReadIntervalMs,
                Ports = s.Ports == null ? new List<int>() : new List<int>(s.Ports),
                UpdatedAtUtc = s.UpdatedAtUtc,
            };
        }

        private string DescribeEndpoint()
        {
            if (!string.IsNullOrWhiteSpace(_resolvedEndpoint)) return _resolvedEndpoint;
            var endpoint = Clean(_config.Endpoint);
            if (!string.IsNullOrWhiteSpace(endpoint))
                return endpoint + (_config.Port > 0 && !endpoint.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ? ":" + _config.Port : string.Empty);
            return string.Equals(ReadString("connection", "com"), "com", StringComparison.OrdinalIgnoreCase) ? "AUTO-COM" : string.Empty;
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


        private int ReconnectDelay(int consecutiveFailures)
        {
            var baseDelay = ReadInt("reconnectDelayMs", 1000, 250, 60000);
            var maxDelay = ReadInt("reconnectMaxDelayMs", 15000, baseDelay, 300000);
            if (consecutiveFailures <= 1) return baseDelay;

            var exponent = Math.Min(consecutiveFailures - 1, 4);
            var calculated = (long)baseDelay << exponent;
            return (int)Math.Min(maxDelay, calculated);
        }

        private void Wait(int milliseconds)
        {
            if (milliseconds <= 0 || _cts.IsCancellationRequested) return;

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
