using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using NSPGatekeeper.Controller.Infrastructure.Logging;

namespace NSPGatekeeper.Controller.Infrastructure.Discovery
{
    public sealed class NspDiscoveryResult
    {
        public string ServiceName { get; set; }
        public string IpAddress { get; set; }
        /// <summary>Actual Core API port read from TXT property "port".</summary>
        public int Port { get; set; }
        /// <summary>Discovery Service SRV port, normally 9000.</summary>
        public int DiscoveryPort { get; set; }
        public string AuthPath { get; set; }
        public string BaseUrl { get; set; }
        public Dictionary<string, string> Properties { get; set; }
        public bool IsNspCoreApi { get; set; }
        public string Message { get; set; }
    }

    /// <summary>
    /// Small DNS-SD/mDNS browser used only as a fallback when the saved Core API URL
    /// cannot be reached. The browser discovers candidate NSP Core API services.
    /// Authentication is performed by CoreApiClient before a discovered URL is saved.
    /// </summary>
    public sealed class ZeroconfDiscoveryClient
    {
        private const int MdnsPort = 5353;
        private static readonly IPAddress MdnsAddress = IPAddress.Parse("224.0.0.251");
        private readonly FileLogger _logger;

        public ZeroconfDiscoveryClient(FileLogger logger)
        {
            _logger = logger;
        }

        public IList<NspDiscoveryResult> Discover(int timeoutMs, string serviceType)
        {
            timeoutMs = Math.Max(1000, timeoutMs <= 0 ? 5000 : timeoutMs);
            var normalizedServiceType = NormalizeServiceType(serviceType);
            var candidates = new Dictionary<string, ServiceCandidate>(StringComparer.OrdinalIgnoreCase);
            var localAddresses = GetDiscoveryAddresses();

            if (_logger != null)
            {
                _logger.Info(
                    "zeroconf",
                    "mDNS discovery starting",
                    "service=" + normalizedServiceType +
                    " interfaces=" + (localAddresses.Count == 0 ? "none" : string.Join(",", localAddresses.Select(x => x.ToString()))));
            }

            if (localAddresses.Count == 0)
            {
                if (_logger != null)
                    _logger.Warn("zeroconf", "No usable IPv4 LAN interface found for mDNS discovery", null);
                return new List<NspDiscoveryResult>();
            }

            // First use standard mDNS: source/listen port 5353, multicast response (QM).
            // Some responders, including python-zeroconf, are most reliable in this mode.
            var standardBudget = Math.Max(1000, (int)(timeoutMs * 0.65));
            var standardSucceeded = DiscoverPass(
                candidates,
                localAddresses,
                normalizedServiceType,
                standardBudget,
                bindMdnsPort: true,
                requestUnicastResponse: false,
                modeName: "QM");

            // If no SRV/port was resolved, retry using QU from an ephemeral port. This
            // covers hosts where UDP/5353 cannot be shared with Bonjour/other mDNS stacks.
            if (!candidates.Values.Any(x => x != null && x.Port > 0))
            {
                var remaining = Math.Max(1000, timeoutMs - standardBudget);
                if (_logger != null)
                    _logger.Warn(
                        "zeroconf",
                        standardSucceeded
                            ? "No resolved NSP service from standard mDNS; trying unicast-response fallback"
                            : "Standard mDNS listener unavailable; trying unicast-response fallback",
                        "timeout_ms=" + remaining);

                DiscoverPass(
                    candidates,
                    localAddresses,
                    normalizedServiceType,
                    remaining,
                    bindMdnsPort: false,
                    requestUnicastResponse: true,
                    modeName: "QU");
            }

            var results = candidates.Values
                .Select(BuildResult)
                .Where(x => x != null && x.IsNspCoreApi)
                .GroupBy(x => x.BaseUrl, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            if (_logger != null)
            {
                var rawCandidates = candidates.Values.Count(x => x != null && x.Port > 0);
                var ptrCandidates = candidates.Values.Count(x => x != null && !string.IsNullOrWhiteSpace(x.InstanceName));
                _logger.Info(
                    "zeroconf",
                    "NSP Core API discovery completed",
                    "count=" + results.Count +
                    " raw_candidates=" + rawCandidates +
                    " ptr_candidates=" + ptrCandidates);
            }
            return results;
        }

        private bool DiscoverPass(
            Dictionary<string, ServiceCandidate> candidates,
            IList<IPAddress> localAddresses,
            string serviceType,
            int timeoutMs,
            bool bindMdnsPort,
            bool requestUnicastResponse,
            string modeName)
        {
            UdpClient udp = null;
            try
            {
                udp = new UdpClient(AddressFamily.InterNetwork);
                udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                udp.Client.ExclusiveAddressUse = false;
                udp.Client.ReceiveTimeout = 300;

                var localPort = bindMdnsPort ? MdnsPort : 0;
                udp.Client.Bind(new IPEndPoint(IPAddress.Any, localPort));

                if (bindMdnsPort)
                {
                    foreach (var localIp in localAddresses)
                    {
                        try
                        {
                            udp.JoinMulticastGroup(MdnsAddress, localIp);
                        }
                        catch (SocketException ex)
                        {
                            if (_logger != null)
                                _logger.Warn("zeroconf", "Failed to join mDNS multicast group", "interface=" + localIp + " error=" + ex.Message);
                        }
                    }
                }

                var bound = (IPEndPoint)udp.Client.LocalEndPoint;
                if (_logger != null)
                    _logger.Info("zeroconf", "mDNS listener ready", "mode=" + modeName + " local_port=" + bound.Port);

                var ptrQuery = BuildQuery(serviceType, 12, requestUnicastResponse);
                SendOnAllInterfaces(udp, ptrQuery, localAddresses, modeName, serviceType, "PTR");

                var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                var resendAt = DateTime.UtcNow.AddMilliseconds(Math.Max(400, timeoutMs / 2));
                var resent = false;
                var detailQueries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var hostQueries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                while (DateTime.UtcNow < deadline)
                {
                    if (!resent && DateTime.UtcNow >= resendAt)
                    {
                        SendOnAllInterfaces(udp, ptrQuery, localAddresses, modeName, serviceType, "PTR");
                        resent = true;
                    }

                    try
                    {
                        IPEndPoint remote = null;
                        var buffer = udp.Receive(ref remote);
                        if (_logger != null)
                            _logger.Info("zeroconf", "mDNS packet received", "mode=" + modeName + " remote=" + remote + " bytes=" + buffer.Length);
                        ParseMessage(buffer, remote, candidates);

                        // Some mDNS responders return only PTR initially. Resolve the service
                        // instance explicitly so SRV/TXT and then A are always requested.
                        foreach (var c in candidates.Values.ToList())
                        {
                            if (c == null || string.IsNullOrWhiteSpace(c.InstanceName)) continue;
                            if (c.InstanceName.IndexOf("._nsp._tcp.local", StringComparison.OrdinalIgnoreCase) >= 0 && c.Port <= 0)
                            {
                                var key = "instance:" + c.InstanceName;
                                if (detailQueries.Add(key))
                                {
                                    SendOnAllInterfaces(udp, BuildQuery(c.InstanceName, 33, requestUnicastResponse), localAddresses, modeName, c.InstanceName, "SRV");
                                    SendOnAllInterfaces(udp, BuildQuery(c.InstanceName, 16, requestUnicastResponse), localAddresses, modeName, c.InstanceName, "TXT");
                                }
                            }

                            if (!string.IsNullOrWhiteSpace(c.TargetHost) && string.IsNullOrWhiteSpace(c.IpAddress))
                            {
                                var host = c.TargetHost.TrimEnd('.');
                                if (hostQueries.Add(host))
                                    SendOnAllInterfaces(udp, BuildQuery(host, 1, requestUnicastResponse), localAddresses, modeName, host, "A");
                            }
                        }
                    }
                    catch (SocketException ex)
                    {
                        if (ex.SocketErrorCode != SocketError.TimedOut && _logger != null)
                            _logger.Warn("zeroconf", "mDNS receive failed", "mode=" + modeName + " error=" + ex.Message);
                    }
                }
                return true;
            }
            catch (SocketException ex)
            {
                if (_logger != null)
                    _logger.Warn("zeroconf", "Unable to create mDNS listener", "mode=" + modeName + " error=" + ex.Message);
                return false;
            }
            finally
            {
                if (udp != null)
                {
                    try { udp.Close(); }
                    catch (Exception ex)
                    {
                        if (_logger != null) _logger.Warn("zeroconf", "mDNS socket close failed", ex.Message);
                    }
                }
            }
        }

        private void SendOnAllInterfaces(
            UdpClient udp,
            byte[] query,
            IList<IPAddress> localAddresses,
            string modeName,
            string queryName,
            string queryType)
        {
            foreach (var localIp in localAddresses)
            {
                try
                {
                    // Explicitly select every active LAN interface. Without this Windows may
                    // send multicast over VPN/Hyper-V/VMware instead of the physical LAN.
                    udp.Client.SetSocketOption(
                        SocketOptionLevel.IP,
                        SocketOptionName.MulticastInterface,
                        localIp.GetAddressBytes());
                    udp.Send(query, query.Length, new IPEndPoint(MdnsAddress, MdnsPort));
                    if (_logger != null)
                        _logger.Info(
                            "zeroconf",
                            "mDNS query sent",
                            "mode=" + modeName + " interface=" + localIp + " qtype=" + queryType + " name=" + queryName);
                }
                catch (SocketException ex)
                {
                    if (_logger != null)
                        _logger.Warn(
                            "zeroconf",
                            "mDNS query send failed",
                            "mode=" + modeName + " interface=" + localIp + " error=" + ex.Message);
                }
            }
        }

        private static IList<IPAddress> GetDiscoveryAddresses()
        {
            var result = new List<IPAddress>();
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic == null || nic.OperationalStatus != OperationalStatus.Up) continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback || nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;

                    IPInterfaceProperties props;
                    try { props = nic.GetIPProperties(); }
                    catch { continue; }

                    foreach (var ua in props.UnicastAddresses)
                    {
                        var ip = ua == null ? null : ua.Address;
                        if (ip == null || ip.AddressFamily != AddressFamily.InterNetwork) continue;
                        if (!IsUsableLanAddress(ip)) continue;
                        if (!result.Any(x => x.Equals(ip))) result.Add(ip);
                    }
                }
            }
            catch
            {
                // Caller logs a clear message when no usable interface remains.
            }
            return result;
        }

        private static byte[] BuildQuery(string name, ushort queryType, bool requestUnicastResponse)
        {
            var data = new List<byte>();
            data.AddRange(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 });
            WriteName(data, name);
            data.Add((byte)(queryType >> 8));
            data.Add((byte)(queryType & 0xff));
            if (requestUnicastResponse)
            {
                data.Add(0x80); data.Add(0x01); // IN + QU
            }
            else
            {
                data.Add(0x00); data.Add(0x01); // IN + QM
            }
            return data.ToArray();
        }

        private static NspDiscoveryResult BuildResult(ServiceCandidate c)
        {
            // SRV port identifies the Zeroconf/Discovery HTTP service (normally 9000).
            // Core API connection details are intentionally minimal and come from TXT:
            //   port=8069
            //   auth_path=/auth/token
            if (c == null || c.Port <= 0) return null;
            if (c.Properties == null)
                c.Properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var ip = !string.IsNullOrWhiteSpace(c.IpAddress) ? c.IpAddress : c.RemoteIpAddress;
            IPAddress parsedIp;
            if (string.IsNullOrWhiteSpace(ip) || !IPAddress.TryParse(ip, out parsedIp) || parsedIp.AddressFamily != AddressFamily.InterNetwork)
                return null;
            if (!IsUsableLanAddress(parsedIp)) return null;

            string portText;
            string authPath;
            c.Properties.TryGetValue("port", out portText);
            c.Properties.TryGetValue("auth_path", out authPath);

            int coreApiPort;
            if (!int.TryParse((portText ?? string.Empty).Trim(), out coreApiPort) || coreApiPort < 1 || coreApiPort > 65535)
                return null;

            authPath = (authPath ?? string.Empty).Trim();
            if (!string.Equals(authPath, "/auth/token", StringComparison.OrdinalIgnoreCase))
                return null;

            var serviceName = string.IsNullOrWhiteSpace(c.ServiceName) ? ExtractInstanceName(c.InstanceName) : c.ServiceName;
            return new NspDiscoveryResult
            {
                ServiceName = serviceName,
                IpAddress = ip,
                Port = coreApiPort,
                DiscoveryPort = c.Port,
                AuthPath = authPath,
                BaseUrl = "http://" + ip + ":" + coreApiPort,
                Properties = new Dictionary<string, string>(c.Properties, StringComparer.OrdinalIgnoreCase),
                IsNspCoreApi = true,
                Message = "NSP service discovered; Core API endpoint resolved from TXT metadata."
            };
        }

        private static bool IsUsableLanAddress(IPAddress ip)
        {
            if (IPAddress.IsLoopback(ip)) return false;
            var bytes = ip.GetAddressBytes();
            if (bytes.Length != 4) return false;
            if (bytes[0] == 10 || bytes[0] == 127) return bytes[0] != 127;
            if (bytes[0] == 192 && bytes[1] == 168) return true;
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
            if (bytes[0] == 169 && bytes[1] == 254) return true;
            return false;
        }

        private static string ExtractInstanceName(string fullName)
        {
            if (string.IsNullOrWhiteSpace(fullName)) return string.Empty;
            var suffix = "._nsp._tcp.local";
            var name = fullName.TrimEnd('.');
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - suffix.Length);
            return name.Replace("\\032", " ");
        }

        private static string NormalizeServiceType(string serviceType)
        {
            var value = (serviceType ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(value)) value = "_nsp._tcp.local";
            return value.TrimEnd('.');
        }

        private static void WriteName(List<byte> data, string name)
        {
            foreach (var label in name.TrimEnd('.').Split('.'))
            {
                var bytes = Encoding.UTF8.GetBytes(label);
                data.Add((byte)bytes.Length);
                data.AddRange(bytes);
            }
            data.Add(0x00);
        }

        private static void ParseMessage(byte[] buffer, IPEndPoint remote, Dictionary<string, ServiceCandidate> candidates)
        {
            if (buffer == null || buffer.Length < 12) return;
            var offset = 4;
            var qd = ReadUInt16(buffer, ref offset);
            var an = ReadUInt16(buffer, ref offset);
            var ns = ReadUInt16(buffer, ref offset);
            var ar = ReadUInt16(buffer, ref offset);

            offset = 12;
            for (var i = 0; i < qd; i++)
            {
                ReadName(buffer, ref offset);
                offset += 4;
                if (offset > buffer.Length) return;
            }

            var total = an + ns + ar;
            for (var i = 0; i < total; i++)
            {
                var name = ReadName(buffer, ref offset);
                if (offset + 10 > buffer.Length) return;
                var type = ReadUInt16(buffer, ref offset);
                offset += 2; // class
                offset += 4; // ttl
                var rdlen = ReadUInt16(buffer, ref offset);
                if (offset + rdlen > buffer.Length) return;
                var rdataOffset = offset;
                offset += rdlen;

                if (type == 12) // PTR
                {
                    var temp = rdataOffset;
                    var instance = ReadName(buffer, ref temp);
                    var c = GetCandidate(candidates, instance);
                    c.InstanceName = instance;
                    c.ServiceName = ExtractInstanceName(instance);
                    c.RemoteIpAddress = remote == null ? null : remote.Address.ToString();
                }
                else if (type == 33) // SRV
                {
                    var temp = rdataOffset;
                    temp += 4; // priority + weight
                    var port = ReadUInt16(buffer, ref temp);
                    var target = ReadName(buffer, ref temp);
                    var c = GetCandidate(candidates, name);
                    c.InstanceName = name;
                    c.ServiceName = ExtractInstanceName(name);
                    c.TargetHost = target.TrimEnd('.');
                    c.Port = port;
                    ServiceCandidate hostCandidate;
                    if (candidates.TryGetValue(c.TargetHost, out hostCandidate) && !string.IsNullOrWhiteSpace(hostCandidate.IpAddress))
                        c.IpAddress = hostCandidate.IpAddress;
                    c.RemoteIpAddress = remote == null ? null : remote.Address.ToString();
                }
                else if (type == 16) // TXT
                {
                    var c = GetCandidate(candidates, name);
                    c.InstanceName = name;
                    c.ServiceName = ExtractInstanceName(name);
                    c.RemoteIpAddress = remote == null ? null : remote.Address.ToString();
                    var end = rdataOffset + rdlen;
                    var temp = rdataOffset;
                    while (temp < end)
                    {
                        var len = buffer[temp++];
                        if (temp + len > end) break;
                        var text = Encoding.UTF8.GetString(buffer, temp, len);
                        temp += len;
                        var eq = text.IndexOf('=');
                        if (eq > 0) c.Properties[text.Substring(0, eq)] = text.Substring(eq + 1);
                    }
                }
                else if (type == 1 && rdlen == 4) // A
                {
                    var ip = new IPAddress(new[] { buffer[rdataOffset], buffer[rdataOffset + 1], buffer[rdataOffset + 2], buffer[rdataOffset + 3] }).ToString();
                    foreach (var c in candidates.Values.Where(x => string.Equals((x.TargetHost ?? string.Empty).TrimEnd('.'), name.TrimEnd('.'), StringComparison.OrdinalIgnoreCase)))
                        c.IpAddress = ip;
                    var hostCandidate = GetCandidate(candidates, name);
                    hostCandidate.IpAddress = ip;
                }
            }
        }

        private static ServiceCandidate GetCandidate(Dictionary<string, ServiceCandidate> candidates, string key)
        {
            key = string.IsNullOrWhiteSpace(key) ? Guid.NewGuid().ToString("N") : key.TrimEnd('.');
            ServiceCandidate c;
            if (!candidates.TryGetValue(key, out c))
            {
                c = new ServiceCandidate { InstanceName = key, Properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) };
                candidates[key] = c;
            }
            return c;
        }

        private static ushort ReadUInt16(byte[] buffer, ref int offset)
        {
            if (offset + 2 > buffer.Length) return 0;
            var value = (ushort)((buffer[offset] << 8) | buffer[offset + 1]);
            offset += 2;
            return value;
        }

        private static string ReadName(byte[] buffer, ref int offset)
        {
            var labels = new List<string>();
            var jumped = false;
            var originalOffset = offset;
            var jumps = 0;
            while (offset < buffer.Length && jumps < 16)
            {
                var len = buffer[offset++];
                if (len == 0) break;
                if ((len & 0xC0) == 0xC0)
                {
                    if (offset >= buffer.Length) break;
                    var pointer = ((len & 0x3F) << 8) | buffer[offset++];
                    if (!jumped) originalOffset = offset;
                    offset = pointer;
                    jumped = true;
                    jumps++;
                    continue;
                }
                if (offset + len > buffer.Length) break;
                labels.Add(Encoding.UTF8.GetString(buffer, offset, len));
                offset += len;
            }
            if (jumped) offset = originalOffset;
            return string.Join(".", labels);
        }

        private sealed class ServiceCandidate
        {
            public string InstanceName { get; set; }
            public string ServiceName { get; set; }
            public string TargetHost { get; set; }
            public string IpAddress { get; set; }
            public string RemoteIpAddress { get; set; }
            public int Port { get; set; }
            public Dictionary<string, string> Properties { get; set; }
        }
    }
}
