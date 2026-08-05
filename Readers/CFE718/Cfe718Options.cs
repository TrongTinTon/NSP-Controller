using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO.Ports;
using System.Linq;
using NSPGatekeeper.Controller.Domain;
using NSPGatekeeper.Controller.Readers.CFE718.Sdk;

namespace NSPGatekeeper.Controller.Readers.CFE718
{
    internal sealed class Cfe718Options
    {
        private static readonly int[] Ports = { 1, 2, 3, 4 };
        private readonly ReaderDeviceConfig _config;

        internal Cfe718Options(ReaderDeviceConfig config)
        {
            _config = config ?? throw new ArgumentNullException("config");
        }

        internal ReaderDeviceConfig Config { get { return _config; } }
        internal IList<int> HardwarePorts { get { return new List<int>(Ports); } }
        internal string Endpoint { get { return Clean(_config.Endpoint); } }
        internal int TcpPort { get { return _config.Port > 0 ? _config.Port : ReadInt("port", 4001, 1, 65535); } }
        internal byte ComAddress { get { return ReadByte("comAddr", 0xFF); } }
        internal byte Baud { get { return ReadByte("baud", 6); } }
        internal byte QValue { get { return ReadByte("q", 4); } }
        internal byte Session { get { return ReadByte("session", 0); } }
        internal byte Target { get { return ReadByte("target", 0); } }
        internal byte FastFlag { get { return ReadByte("fast", 1); } }
        internal int PortDelayMs { get { return ReadInt("portDelayMs", 3, 0, 1000); } }
        internal int LoopDelayMs { get { return ReadInt("loopDelayMs", 1, 0, 1000); } }
        internal int ShutdownTimeoutMs { get { return ReadInt("shutdownTimeoutMs", 10000, 1000, 60000); } }
        internal int ReconnectDelayMs { get { return ReadInt("reconnectDelayMs", 1000, 250, 60000); } }
        internal int ReconnectMaxDelayMs { get { return ReadInt("reconnectMaxDelayMs", 15000, ReconnectDelayMs, 300000); } }
        internal byte ScanTime
        {
            get
            {
                var units = (int)Math.Ceiling(Math.Max(1, _config.ReadIntervalMs) / 100.0);
                return ClampByte(units, 1, 255);
            }
        }

        internal UhfReader288ConnectionKind ConnectionKind
        {
            get
            {
                var endpoint = Endpoint;
                var fallback = string.IsNullOrWhiteSpace(endpoint)
                    || endpoint.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                    ? "com"
                    : "tcp";
                var value = ReadString("connection", fallback).Trim().ToLowerInvariant();
                if (value == "com") return UhfReader288ConnectionKind.Com;
                if (value == "tcp") return UhfReader288ConnectionKind.Tcp;
                throw new InvalidOperationException("Unsupported Reader connection type: " + value);
            }
        }

        internal int ComPort
        {
            get
            {
                var value = ParseComPort(Endpoint);
                if (value <= 0)
                    throw new InvalidOperationException("Physical Reader COM endpoint is required for an active discovery runtime.");
                return value;
            }
        }

        internal string Describe()
        {
            return "observation_serial=" + (_config.SerialNumber ?? "<empty>")
                + "; driver=" + (_config.DriverKey ?? "<empty>")
                + "; connection=" + ConnectionKind.ToString().ToLowerInvariant()
                + "; endpoint=" + (string.IsNullOrWhiteSpace(Endpoint) ? "AUTO-COM" : Endpoint)
                + "; tcp_port=" + _config.Port
                + "; scan_ports=" + string.Join(",", HardwarePorts)
                + "; port_processing=edge"
                + "; power_dbm=" + _config.PowerDbm
                + "; read_interval_ms=" + _config.ReadIntervalMs
                + "; tid_start=" + _config.TidStartAddress
                + "; tid_length=" + _config.TidLength
                + "; tid_mode=inventory_g2_per_request"
                + "; config_hash=" + (_config.ConfigHash ?? "<empty>");
        }

        internal byte PortSelector(int portNo, bool alternate)
        {
            var mode = ReadString("portSelectorMode", "sequential").ToLowerInvariant();
            var sequential = (byte)(0x80 + Math.Max(0, portNo - 1));
            var bitmask = (byte)(0x80 | (1 << Math.Max(0, portNo - 1)));
            if (mode == "bitmask") return alternate ? sequential : bitmask;
            return alternate ? bitmask : sequential;
        }

        internal int ReconnectDelay(int consecutiveFailures)
        {
            if (consecutiveFailures <= 1) return ReconnectDelayMs;
            var exponent = Math.Min(consecutiveFailures - 1, 4);
            var calculated = (long)ReconnectDelayMs << exponent;
            return (int)Math.Min(ReconnectMaxDelayMs, calculated);
        }

        internal static int DecodeReportedPort(byte value)
        {
            if (value >= 0x80 && value <= 0x8F) return (value & 0x0F) + 1;
            if (value == 1 || value == 2 || value == 4 || value == 8)
                return value == 1 ? 1 : value == 2 ? 2 : value == 4 ? 3 : 4;
            return value > 0 ? value : 0;
        }

        internal static bool IsReaderPort(int portNo)
        {
            return portNo >= 1 && portNo <= 16;
        }

        internal static string WindowsComPorts()
        {
            try
            {
                var values = SerialPort.GetPortNames()
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Select(item => item.Trim().ToUpperInvariant())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                return values.Length == 0 ? "none" : string.Join(",", values);
            }
            catch (Exception ex)
            {
                return "enumeration_failed:" + ex.GetType().Name + ":" + ex.Message;
            }
        }

        internal static int ParseComPort(string endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint)) return 0;
            var text = endpoint.Trim();
            if (text.StartsWith("COM", StringComparison.OrdinalIgnoreCase)) text = text.Substring(3);
            int value;
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? value : 0;
        }

        internal static byte ClampByte(int value, int min, int max)
        {
            return (byte)Math.Max(min, Math.Min(max, value));
        }

        private string ReadString(string key, string fallback)
        {
            string value;
            return _config.Options != null
                && _config.Options.TryGetValue(key, out value)
                && !string.IsNullOrWhiteSpace(value)
                ? value.Trim()
                : fallback;
        }

        private int ReadInt(string key, int fallback, int min, int max)
        {
            int value;
            if (!int.TryParse(ReadString(key, fallback.ToString(CultureInfo.InvariantCulture)), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                value = fallback;
            return Math.Max(min, Math.Min(max, value));
        }

        private byte ReadByte(string key, int fallback)
        {
            return ClampByte(ReadInt(key, fallback, 0, 255), 0, 255);
        }

        private static string Clean(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }
}
