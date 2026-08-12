using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using NSPGatekeeper.Controller.Domain;
using NSPGatekeeper.Controller.Infrastructure.Logging;
using NSPGatekeeper.Controller.Readers;
using NSPGatekeeper.Controller.Readers.CFE718.Sdk;

namespace NSPGatekeeper.Controller.Readers.CFE718
{
    internal sealed class Cfe718ReaderRuntime : IReaderRuntime
    {
        private readonly FileLogger _logger;
        private readonly object _statusGate = new object();
        private readonly object _detectionLogGate = new object();
        private readonly object _configurationGate = new object();
        private readonly Dictionary<string, DateTime> _detectionLogAt = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly ReaderDeviceConfig _config;
        private readonly Cfe718Options _options;
        private readonly Cfe718Inventory _inventory;
        private Thread _thread;
        private bool _disposed;
        private ReaderStatus _status;
        private string _resolvedEndpoint;
        private string _hardwareSerialNumber;
        private string _lastFailureSignature;
        private DateTime _lastFullFailureAtUtc;
        private string _lastPortFailureSignature;
        private DateTime _lastPortFailureLogAtUtc;
        private long _desiredConfigurationVersion = 1;
        private long _appliedConfigurationVersion;
        private ReaderAppliedConfiguration _appliedConfiguration;

        internal Cfe718ReaderRuntime(ReaderDeviceConfig config, FileLogger logger)
        {
            _config = config ?? throw new ArgumentNullException("config");
            _logger = logger;
            _options = new Cfe718Options(_config);
            _inventory = new Cfe718Inventory(_options);
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
                    _options.Describe() + "; sdk=" + UhfReader288Sdk.DescribeRuntime());

            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "RFID " + _config.SerialNumber
            };
            _thread.Start();
        }

        public void Stop()
        {
            _cts.Cancel();
            var thread = _thread;
            if (thread == null || !thread.IsAlive || thread == Thread.CurrentThread) return;

            try
            {
                if (!thread.Join(_options.ShutdownTimeoutMs) && _logger != null)
                    _logger.Warn(
                        "reader-runtime",
                        "Reader worker did not stop before timeout",
                        "serial=" + _config.SerialNumber
                        + "; endpoint=" + DescribeEndpoint()
                        + "; timeout_ms=" + _options.ShutdownTimeoutMs);
            }
            catch (Exception ex)
            {
                if (_logger != null)
                    _logger.Error("reader-runtime", "Reader worker stop failed", ex, _options.Describe());
            }
        }

        public bool TryApplyConfiguration(ReaderDeviceConfig config)
        {
            if (config == null || _disposed) return false;

            lock (_configurationGate)
            {
                // Transport, identity and TID inventory shape require a new SDK session.
                if (!string.Equals(_config.DriverKey, config.DriverKey, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(_config.Endpoint, config.Endpoint, StringComparison.OrdinalIgnoreCase)
                    || _config.Port != config.Port
                    || _config.TidStartAddress != config.TidStartAddress
                    || _config.TidLength != config.TidLength)
                    return false;

                var changed = _config.PowerDbm != config.PowerDbm
                    || _config.ReadIntervalMs != config.ReadIntervalMs
                    || !string.Equals(_config.ConfigurationSource, config.ConfigurationSource, StringComparison.OrdinalIgnoreCase);
                _config.PowerDbm = config.PowerDbm;
                _config.ReadIntervalMs = config.ReadIntervalMs;
                _config.ConfigurationSource = config.ConfigurationSource;
                _config.ConfigHash = config.ConfigHash;
                if (changed) _desiredConfigurationVersion++;
                return true;
            }
        }

        public ReaderAppliedConfiguration GetAppliedConfiguration()
        {
            lock (_configurationGate)
            {
                return CloneAppliedConfiguration(_appliedConfiguration);
            }
        }

        private void Run()
        {
            var consecutiveFailures = 0;
            while (!_cts.IsCancellationRequested)
            {
                var phase = "prepare";
                UhfReader288Session session = null;
                var comPort = 0;
                byte comAddress = _options.ComAddress;

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

                    phase = "create_sdk_session";
                    session = UhfReader288Sdk.CreateSession();

                    phase = "open_transport";
                    var requestedConnection = _options.ConnectionKind;
                    var openResult = Open(session, ref comAddress, out comPort);
                    Cfe718ReaderIdentity.EnsureSuccess(
                        "Open " + requestedConnection,
                        openResult,
                        "session_id=" + session.SessionId
                        + "; com_port=" + comPort
                        + "; com_address=" + FormatByte(comAddress)
                        + "; " + _options.Describe());

                    if (_logger != null)
                        _logger.Info(
                            "reader-connect",
                            "Reader transport opened",
                            "connection=" + session.ConnectionKind
                            + "; endpoint=" + DescribeEndpoint()
                            + "; session_id=" + session.SessionId
                            + "; com_port=" + comPort
                            + "; com_address=" + FormatByte(comAddress));

                    phase = "read_sdk_identity";
                    ApplyIdentity(Cfe718ReaderIdentity.Read(session, ref comAddress));

                    phase = "apply_reader_configuration";
                    lock (_configurationGate)
                    {
                        Cfe718ReaderConfiguration.Apply(session, ref comAddress, _options);
                        MarkConfigurationAppliedLocked();
                        _appliedConfigurationVersion = _desiredConfigurationVersion;
                    }
                    if (_logger != null)
                        _logger.Info(
                            "reader-config-apply",
                            "Reader configuration applied",
                            _options.Describe()
                            + "; endpoint=" + DescribeEndpoint()
                            + "; session_id=" + session.SessionId
                            + "; com_address=" + FormatByte(comAddress));

                    phase = "register_callback";
                    session.RegisterTagCallback(OnTagReported);
                    if (_logger != null)
                        _logger.Info(
                            "reader-connect",
                            "RFID callback registered",
                            "serial=" + (_hardwareSerialNumber ?? _config.SerialNumber)
                            + "; endpoint=" + DescribeEndpoint()
                            + "; session_id=" + session.SessionId);

                    consecutiveFailures = 0;
                    _lastFailureSignature = null;
                    SetStatus(true, "inventory_running");
                    if (_logger != null)
                        _logger.Info(
                            "reader-inventory",
                            "RFID inventory started",
                            _options.Describe() + "; endpoint=" + DescribeEndpoint());

                    while (!_cts.IsCancellationRequested)
                    {
                        phase = "apply_pending_reader_configuration";
                        ApplyPendingConfiguration(session, ref comAddress);

                        var acceptedPorts = 0;
                        var failures = new List<string>();
                        foreach (var portNo in _options.HardwarePorts)
                        {
                            if (_cts.IsCancellationRequested) break;
                            phase = "inventory_port_" + portNo.ToString(CultureInfo.InvariantCulture);
                            var result = _inventory.Execute(session, ref comAddress, portNo);
                            if (UhfReader288Result.IsInventoryAccepted(result))
                            {
                                acceptedPorts++;
                            }
                            else
                            {
                                failures.Add("port=" + portNo + ":" + UhfReader288Result.Format(result));
                            }
                            Wait(_options.PortDelayMs);
                        }

                        if (!_cts.IsCancellationRequested && acceptedPorts == 0)
                            throw new InvalidOperationException(
                                "Inventory failed on every Reader Port. " + string.Join(" | ", failures));

                        if (failures.Count > 0) LogPartialPortFailures(failures, acceptedPorts);
                        Wait(_options.LoopDelayMs);
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
                    var connectionKind = session == null
                        || session.ConnectionKind == UhfReader288ConnectionKind.None
                        ? _options.ConnectionKind
                        : session.ConnectionKind;
                    var sessionId = session == null ? 0 : session.SessionId;
                    var context = "phase=" + phase
                        + "; diagnosis=" + DiagnoseFailure(phase, root)
                        + "; attempt=" + consecutiveFailures.ToString(CultureInfo.InvariantCulture)
                        + "; connection=" + connectionKind
                        + "; endpoint=" + DescribeEndpoint()
                        + "; session_id=" + sessionId
                        + "; com_port=" + comPort
                        + "; com_address=" + FormatByte(comAddress)
                        + "; windows_com=" + Cfe718Options.WindowsComPorts()
                        + "; " + _options.Describe()
                        + "; sdk=" + UhfReader288Sdk.DescribeRuntime();
                    SetStatus(false, "reconnecting: " + root.Message);
                    LogConnectionFailure(root, context);
                }
                finally
                {
                    CloseSession(session, comPort);
                }

                if (!_cts.IsCancellationRequested)
                {
                    var delay = _options.ReconnectDelay(consecutiveFailures);
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
                _logger.Info("reader-runtime", "Reader worker stopped", _options.Describe());
        }

        private void ApplyPendingConfiguration(UhfReader288Session session, ref byte comAddress)
        {
            lock (_configurationGate)
            {
                if (_desiredConfigurationVersion == _appliedConfigurationVersion) return;
                Cfe718ReaderConfiguration.Apply(session, ref comAddress, _options);
                MarkConfigurationAppliedLocked();
                _appliedConfigurationVersion = _desiredConfigurationVersion;
            }

            SetStatus(true, "inventory_running");

            if (_logger != null)
                _logger.Info(
                    "reader-config-apply",
                    "Reader runtime parameters updated without restarting acquisition",
                    "serial=" + (_hardwareSerialNumber ?? _config.SerialNumber)
                    + "; endpoint=" + DescribeEndpoint()
                    + "; power_dbm=" + _config.PowerDbm.ToString(CultureInfo.InvariantCulture)
                    + "; read_interval_ms=" + _config.ReadIntervalMs.ToString(CultureInfo.InvariantCulture)
                    + "; callback_preserved=true");
        }

        private int Open(UhfReader288Session session, ref byte comAddress, out int comPort)
        {
            comPort = 0;
            if (_options.ConnectionKind == UhfReader288ConnectionKind.Com)
            {
                comPort = _options.ComPort;
                _resolvedEndpoint = "COM" + comPort.ToString(CultureInfo.InvariantCulture);
                return session.OpenComPort(comPort, ref comAddress, _options.Baud);
            }

            var endpoint = _options.Endpoint;
            if (string.IsNullOrWhiteSpace(endpoint))
                throw new InvalidOperationException("Reader TCP endpoint is required.");

            _resolvedEndpoint = endpoint + ":" + _options.TcpPort;
            return session.OpenNetPort(_options.TcpPort, endpoint, ref comAddress);
        }

        private void CloseSession(UhfReader288Session session, int comPort)
        {
            if (session == null) return;

            try
            {
                var connection = session.ConnectionKind;
                if (connection == UhfReader288ConnectionKind.None) return;

                var result = session.Close();
                if (_logger != null)
                {
                    var detail = "connection=" + connection
                        + "; endpoint=" + DescribeEndpoint()
                        + "; session_id=" + session.SessionId
                        + "; com_port=" + comPort
                        + "; result=" + UhfReader288Result.Format(result);
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
                        "endpoint=" + DescribeEndpoint()
                        + "; session_id=" + session.SessionId
                        + "; com_port=" + comPort);
            }
            finally
            {
                session.Dispose();
            }
        }

        private void ApplyIdentity(Cfe718ReaderIdentity identity)
        {
            _hardwareSerialNumber = identity.SerialNumber;
            lock (_statusGate)
            {
                _status.SerialNumber = identity.SerialNumber;
                _status.DetectedSdkSerialNumber = identity.SerialNumber;
                _status.DetectedEndpoint = DescribeEndpoint();
                _status.Model = "CF-E718";
                if (!string.IsNullOrWhiteSpace(identity.FirmwareVersion))
                    _status.FirmwareVersion = identity.FirmwareVersion;
                _status.UpdatedAtUtc = DateTime.UtcNow;
            }

            if (_logger != null)
                _logger.Info(
                    "reader-identity",
                    "Reader SDK identity observed",
                    "serial=" + identity.SerialNumber
                    + "; raw_serial=" + identity.RawSerial
                    + "; firmware=" + (identity.FirmwareVersion ?? "<unknown>")
                    + "; endpoint=" + DescribeEndpoint());
        }

        private void OnTagReported(UhfReader288Tag tag)
        {
            try
            {
                if (tag == null) return;
                var tid = Clean(tag.Uid);
                if (string.IsNullOrWhiteSpace(tid)) return;

                var portNo = Cfe718Options.DecodeReportedPort(tag.Antenna);
                if (!Cfe718Options.IsReaderPort(portNo))
                {
                    if (_logger != null)
                        _logger.Warn(
                            "reader-callback",
                            "Dropped RFID detection because SDK did not report a valid port_no",
                            "serial=" + _config.SerialNumber
                            + "; sdk_ant=" + tag.Antenna.ToString(CultureInfo.InvariantCulture));
                    return;
                }

                var detection = new RfidDetection
                {
                    SerialNumber = _hardwareSerialNumber ?? _status.SerialNumber ?? _config.SerialNumber,
                    PortNo = portNo,
                    Tid = tid.ToUpperInvariant(),
                    RssiDbm = Convert.ToDouble(tag.Rssi, CultureInfo.InvariantCulture),
                    DetectedAtUtc = DateTime.UtcNow
                };

                var handler = DetectionReceived;
                if (handler != null) handler(detection);
                LogDetection(detection);
            }
            catch (Exception ex)
            {
                if (_logger != null)
                    _logger.Error(
                        "reader-callback",
                        "RFID callback parse failed",
                        ex,
                        "serial=" + _config.SerialNumber + "; endpoint=" + DescribeEndpoint());
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
                    foreach (var oldKey in _detectionLogAt.Where(item => item.Value < cutoff).Select(item => item.Key).ToList())
                        _detectionLogAt.Remove(oldKey);
                }
            }

            _logger.Info(
                "reader-detection",
                "Detected RFID TID",
                "serial=" + detection.SerialNumber
                + "; port=" + detection.PortNo.ToString(CultureInfo.InvariantCulture)
                + "; tid=" + detection.Tid
                + "; rssi=" + (detection.RssiDbm.HasValue
                    ? detection.RssiDbm.Value.ToString(CultureInfo.InvariantCulture)
                    : string.Empty));
        }

        private void LogPartialPortFailures(IList<string> failures, int acceptedPorts)
        {
            if (_logger == null || failures == null || failures.Count == 0) return;
            var signature = string.Join("|", failures);
            var now = DateTime.UtcNow;
            if (string.Equals(signature, _lastPortFailureSignature, StringComparison.Ordinal)
                && (now - _lastPortFailureLogAtUtc).TotalSeconds < 60)
                return;

            _lastPortFailureSignature = signature;
            _lastPortFailureLogAtUtc = now;
            _logger.Warn(
                "reader-inventory",
                "One or more Reader hardware ports returned SDK errors; remaining ports continue",
                "serial=" + (_hardwareSerialNumber ?? _config.SerialNumber)
                + "; endpoint=" + DescribeEndpoint()
                + "; accepted_ports=" + acceptedPorts
                + "; failures=" + signature
                + "; port_processing=edge");
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
                DetectedSdkSerialNumber = _config.SerialNumber,
                DetectedEndpoint = _config.Endpoint,
                Model = "CF-E718",
                Endpoint = DescribeEndpoint(),
                Online = online,
                Message = message,
                PowerDbm = 0,
                ReadIntervalMs = 0,
                TidStartAddress = 0,
                TidLength = 0,
                ConfigurationApplied = false,
                ConfigurationSource = _config.ConfigurationSource ?? "Default",
                AppliedConfigHash = null,
                ConfigurationAppliedAtUtc = null,
                Ports = _options.HardwarePorts,
                UpdatedAtUtc = DateTime.UtcNow
            };
        }

        private static ReaderStatus CloneStatus(ReaderStatus source)
        {
            return new ReaderStatus
            {
                DriverKey = source.DriverKey,
                SerialNumber = source.SerialNumber,
                DetectedSdkSerialNumber = source.DetectedSdkSerialNumber,
                DetectedEndpoint = source.DetectedEndpoint,
                Model = source.Model,
                Endpoint = source.Endpoint,
                Online = source.Online,
                Message = source.Message,
                FirmwareVersion = source.FirmwareVersion,
                PowerDbm = source.PowerDbm,
                ReadIntervalMs = source.ReadIntervalMs,
                TidStartAddress = source.TidStartAddress,
                TidLength = source.TidLength,
                ConfigurationApplied = source.ConfigurationApplied,
                ConfigurationSource = source.ConfigurationSource,
                AppliedConfigHash = source.AppliedConfigHash,
                ConfigurationAppliedAtUtc = source.ConfigurationAppliedAtUtc,
                Ports = source.Ports == null ? new List<int>() : new List<int>(source.Ports),
                UpdatedAtUtc = source.UpdatedAtUtc
            };
        }

        private void MarkConfigurationAppliedLocked()
        {
            var applied = BuildAppliedConfigurationLocked();
            _appliedConfiguration = applied;
            ApplyConfigurationToStatus(applied);
        }

        private ReaderAppliedConfiguration BuildAppliedConfigurationLocked()
        {
            return new ReaderAppliedConfiguration
            {
                Source = string.IsNullOrWhiteSpace(_config.ConfigurationSource) ? "Default" : _config.ConfigurationSource.Trim(),
                PowerDbm = Cfe718Options.ClampByte(_config.PowerDbm, 0, 33),
                ReadIntervalMs = Math.Max(100, _options.ScanTime * 100),
                TidStartAddress = Cfe718Options.ClampByte(_config.TidStartAddress, 0, 255),
                TidLength = Cfe718Options.ClampByte(_config.TidLength, 1, 15),
                ConfigHash = _config.ConfigHash,
                AppliedAtUtc = DateTime.UtcNow,
            };
        }

        private void ApplyConfigurationToStatus(ReaderAppliedConfiguration applied)
        {
            if (applied == null) return;
            lock (_statusGate)
            {
                _status.PowerDbm = applied.PowerDbm;
                _status.ReadIntervalMs = applied.ReadIntervalMs;
                _status.TidStartAddress = applied.TidStartAddress;
                _status.TidLength = applied.TidLength;
                _status.ConfigurationApplied = true;
                _status.ConfigurationSource = applied.Source;
                _status.AppliedConfigHash = applied.ConfigHash;
                _status.ConfigurationAppliedAtUtc = applied.AppliedAtUtc;
                _status.UpdatedAtUtc = DateTime.UtcNow;
            }
        }

        private static ReaderAppliedConfiguration CloneAppliedConfiguration(ReaderAppliedConfiguration source)
        {
            if (source == null) return null;
            return new ReaderAppliedConfiguration
            {
                Source = source.Source,
                PowerDbm = source.PowerDbm,
                ReadIntervalMs = source.ReadIntervalMs,
                TidStartAddress = source.TidStartAddress,
                TidLength = source.TidLength,
                ConfigHash = source.ConfigHash,
                AppliedAtUtc = source.AppliedAtUtc,
            };
        }

        private string ConnectionAttemptDescription(byte comAddress)
        {
            return _options.Describe()
                + "; physical_endpoint=" + (_options.Endpoint ?? "AUTO-COM")
                + "; com_address=" + FormatByte(comAddress)
                + "; windows_com=" + Cfe718Options.WindowsComPorts()
                + "; sdk=" + UhfReader288Sdk.DescribeRuntime();
        }

        private string DescribeEndpoint()
        {
            if (!string.IsNullOrWhiteSpace(_resolvedEndpoint)) return _resolvedEndpoint;
            var endpoint = Clean(_config.Endpoint);
            if (!string.IsNullOrWhiteSpace(endpoint))
                return endpoint + (_config.Port > 0
                    && !endpoint.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                    ? ":" + _config.Port
                    : string.Empty);
            return _options.ConnectionKind == UhfReader288ConnectionKind.Com ? "AUTO-COM" : string.Empty;
        }

        private static string DiagnoseFailure(string phase, Exception ex)
        {
            var message = ex == null ? string.Empty : ex.Message ?? string.Empty;
            if (ex is DllNotFoundException)
                return "sdk_dll_missing_or_not_in_output; verify managed UHFReader288.dll beside the executable";
            if (ex is BadImageFormatException)
                return "sdk_architecture_mismatch; deploy x86 C# SDK with x86 build or x64 C# SDK with x64 build";
            if (ex is TypeLoadException || ex is MissingMethodException)
                return "sdk_api_mismatch; deployed UHFReader288.dll does not match the C# SDK V2.1 adapter";
            if (message.IndexOf("not currently reported by Windows", StringComparison.OrdinalIgnoreCase) >= 0)
                return "configured_com_not_detected_by_windows";
            if (string.Equals(phase, "create_sdk_session", StringComparison.OrdinalIgnoreCase))
                return "managed_sdk_load_or_reader_instance_failed";
            if (string.Equals(phase, "open_transport", StringComparison.OrdinalIgnoreCase))
                return "transport_open_failed; check Windows COM presence, another process holding the port, driver and baud";
            if (string.Equals(phase, "read_sdk_identity", StringComparison.OrdinalIgnoreCase))
                return "transport_opened_but_reader_identity_failed; check cable, power, baud and SDK compatibility";
            if (string.Equals(phase, "apply_reader_configuration", StringComparison.OrdinalIgnoreCase)
                || string.Equals(phase, "apply_pending_reader_configuration", StringComparison.OrdinalIgnoreCase))
                return "reader_configuration_sdk_command_failed; inspect Reader-wide RF power and inventory scan time";
            if (string.Equals(phase, "register_callback", StringComparison.OrdinalIgnoreCase))
                return "reader_connected_but_callback_registration_failed; verify C# SDK callback signature";
            if (!string.IsNullOrWhiteSpace(phase)
                && phase.StartsWith("inventory_port_", StringComparison.OrdinalIgnoreCase))
                return "inventory_failed_after_connect; Reader may have disconnected or SDK returned a runtime error";
            return "unexpected_reader_runtime_failure; inspect exception_type and stack";
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

        private void Wait(int milliseconds)
        {
            if (milliseconds <= 0 || _cts.IsCancellationRequested) return;
            _cts.Token.WaitHandle.WaitOne(milliseconds);
        }

        private static string FormatByte(byte value)
        {
            return value.ToString(CultureInfo.InvariantCulture)
                + " (0x" + value.ToString("X2", CultureInfo.InvariantCulture) + ")";
        }

        private static Exception Unwrap(Exception ex)
        {
            var current = ex;
            while (current is TargetInvocationException && current.InnerException != null)
                current = current.InnerException;
            return current ?? ex;
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
            // Keep the CTS alive until the vendor callback and worker have fully unwound.
        }
    }
}
