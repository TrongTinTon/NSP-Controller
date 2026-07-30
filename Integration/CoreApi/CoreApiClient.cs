using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NSPGatekeeper.Controller.Configuration;
using NSPGatekeeper.Controller.Domain;
using NSPGatekeeper.Controller.Infrastructure.Discovery;
using NSPGatekeeper.Controller.Infrastructure.Logging;

namespace NSPGatekeeper.Controller.Integration.CoreApi
{
    public sealed class CoreApiClient : IDisposable
    {
        private readonly AppSettings _settings;
        private readonly FileLogger _logger;
        private readonly ZeroconfDiscoveryClient _discovery;
        private readonly HttpClient _http;
        private readonly object _authGate = new object();
        private string _accessToken;
        private string _refreshToken;
        private DateTime? _expiresAtUtc;
        private DateTime? _refreshExpiresAtUtc;

        public CoreApiClient(AppSettings settings, FileLogger logger)
        {
            _settings = settings ?? throw new ArgumentNullException("settings");
            _logger = logger;
            _discovery = new ZeroconfDiscoveryClient(logger);
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Max(3, settings.CoreApiTimeoutSec)) };
        }

        public string BaseUrl { get { return AppSettings.NormalizeBaseUrl(_settings.CoreApiBaseUrl); } }

        public bool IsAuthenticated
        {
            get
            {
                lock (_authGate) return IsTokenUsable();
            }
        }

        public CoreApiAuthResult EnsureAuthenticated()
        {
            lock (_authGate)
            {
                if (IsTokenUsable()) return CurrentAuthResult("token_cached");
                ValidateIdentitySettings();

                if (!string.IsNullOrWhiteSpace(_refreshToken) &&
                    (!_refreshExpiresAtUtc.HasValue || _refreshExpiresAtUtc.Value > DateTime.UtcNow.AddMinutes(1)) &&
                    !string.IsNullOrWhiteSpace(BaseUrl))
                {
                    try
                    {
                        return RefreshCurrentServer();
                    }
                    catch (Exception refreshError)
                    {
                        if (_logger != null) _logger.Warn("auth", "Refresh token failed", DescribeException(refreshError));
                        _refreshToken = null;
                        _refreshExpiresAtUtc = null;

                        if (IsConnectivityFailure(refreshError))
                            return AuthenticateWithDiscoveryFallback(refreshError);
                        if (!IsAuthorizationFailure(refreshError))
                            throw;
                    }
                }

                if (!string.IsNullOrWhiteSpace(BaseUrl))
                {
                    try
                    {
                        return AuthenticateAt(BaseUrl, false);
                    }
                    catch (Exception firstError)
                    {
                        if (!IsConnectivityFailure(firstError)) throw;
                        if (!_settings.DiscoveryEnabled) throw BuildConnectionException(BaseUrl, firstError, null);
                        return AuthenticateWithDiscoveryFallback(firstError);
                    }
                }

                if (!_settings.DiscoveryEnabled)
                    throw new InvalidOperationException("Core API Server URL is empty and Zeroconf fallback is disabled.");

                return AuthenticateWithDiscoveryFallback(new InvalidOperationException("No saved Core API Server URL."));
            }
        }

        public bool IsPermanentRequestError(Exception error)
        {
            var http = error as CoreApiHttpException;
            return http != null && http.StatusCode >= 400 && http.StatusCode < 500 &&
                   http.StatusCode != 401 && http.StatusCode != 408 && http.StatusCode != 429;
        }

        public void InvalidateToken()
        {
            lock (_authGate)
            {
                _accessToken = null;
                _refreshToken = null;
                _expiresAtUtc = null;
                _refreshExpiresAtUtc = null;
            }
        }

        public JObject Heartbeat()
        {
            return PostAuthenticated("heartbeat", new JObject { ["controller_code"] = _settings.ControllerCode });
        }

        public IList<ReaderDeviceConfig> PullDeviceConfigs()
        {
            var response = PostAuthenticated("controller/device-config/pull", new JObject
            {
                ["controller_code"] = _settings.ControllerCode
            });

            var data = response["data"] as JObject;
            var devices = data == null ? null : data["devices"] as JArray;
            if (devices == null) return new List<ReaderDeviceConfig>();

            var result = new List<ReaderDeviceConfig>();
            foreach (var token in devices.OfType<JObject>())
            {
                var serial = Clean((string)token["serial_number"]);
                if (string.IsNullOrWhiteSpace(serial))
                {
                    if (_logger != null) _logger.Warn("device-config", "Ignored Reader config without serial_number", token.ToString(Formatting.None));
                    continue;
                }
                serial = serial.ToUpperInvariant();

                var readerParameters = token["reader_parameters"] as JObject ?? new JObject();
                var connection = token["connection"] as JObject ?? new JObject();
                var config = new ReaderDeviceConfig
                {
                    DeviceCode = serial,
                    SerialNumber = serial,
                    DeviceName = Clean((string)token["reader_name"]) ?? serial,
                    Model = Clean((string)token["model_number"]),
                    DriverKey = Clean((string)connection["driver_key"] ?? (string)token["driver_key"]),
                    Endpoint = Clean((string)connection["endpoint"] ?? (string)token["endpoint"]),
                    Port = connection.Value<int?>("port") ?? token.Value<int?>("port") ?? 0,
                    Enabled = true,
                    PowerDbm = readerParameters.Value<int?>("power_dbm") ?? 30,
                    ReadIntervalMs = readerParameters.Value<int?>("read_interval_ms") ?? 200,
                    TidStartAddress = readerParameters.Value<int?>("tid_start_address") ?? 2,
                    TidLength = readerParameters.Value<int?>("tid_length") ?? 4,
                    ConfigHash = Sha256(token.ToString(Formatting.None))
                };

                var options = connection["options"] as JObject ?? token["options"] as JObject;
                if (options != null)
                {
                    foreach (var property in options.Properties())
                        config.Options[property.Name] = property.Value == null ? string.Empty : property.Value.ToString();
                }

                var antennas = token["antennas"] as JArray;
                if (antennas != null)
                {
                    foreach (var ant in antennas.OfType<JObject>())
                    {
                        var antennaNo = ant.Value<int?>("antenna_no") ?? 0;
                        if (antennaNo <= 0) continue;
                        config.Antennas.Add(new ReaderAntennaConfig
                        {
                            AntennaId = antennaNo,
                            Enabled = true
                        });
                    }
                }

                // Current Edge payload is deliberately server-minimal. Physical connection
                // properties may be supplied later or preserved from the local cached profile.
                result.Add(config);
            }
            return result;
        }

        public void ReportDeviceStatus(IList<ReaderStatus> statuses)
        {
            var devices = new JArray();
            foreach (var status in statuses ?? new List<ReaderStatus>())
            {
                if (status == null || string.IsNullOrWhiteSpace(status.SerialNumber)) continue;
                var reportedSeenAt = status.Online ? DateTime.UtcNow : status.UpdatedAtUtc;
                devices.Add(new JObject
                {
                    ["serial_number"] = status.SerialNumber.Trim().ToUpperInvariant(),
                    ["antennas"] = new JArray((status.Antennas ?? new List<int>()).Distinct().OrderBy(x => x)),
                    ["device_status"] = DeviceStatusName(status),
                    // An online status report is itself proof that the Reader is alive now.
                    // Do not reuse the timestamp from the original connect transition, or
                    // Edge's offline cron would eventually mark a healthy Reader offline.
                    ["last_seen_at"] = reportedSeenAt.ToUniversalTime().ToString("o"),
                    ["firmware_version"] = status.FirmwareVersion ?? string.Empty,
                    ["power_dbm"] = Math.Max(0, Math.Min(40, status.PowerDbm)),
                    ["read_interval_ms"] = Math.Max(1, Math.Min(60000, status.ReadIntervalMs))
                });
            }

            PostAuthenticated("devices/report", new JObject
            {
                ["controller_code"] = _settings.ControllerCode,
                ["devices"] = devices
            });
        }

        public void PushDetections(IList<RfidDetection> detections)
        {
            if (detections == null || detections.Count == 0) return;
            var items = new JArray();
            foreach (var detection in detections)
            {
                items.Add(new JObject
                {
                    ["event_uid"] = detection.EventUid,
                    ["serial_number"] = detection.DeviceSerial,
                    ["antenna_no"] = detection.AntennaId,
                    ["detected_at"] = detection.DetectedAtUtc.ToUniversalTime().ToString("o"),
                    ["tid"] = detection.Tid
                });
            }

            PostAuthenticated("parking/detections/push", new JObject
            {
                ["controller_code"] = _settings.ControllerCode,
                ["detections"] = items
            });
        }

        public MeasurementSessionConfig PullMeasurement(string currentMeasurementCode)
        {
            var response = PostAuthenticated("controller/measurement/pull", new JObject
            {
                ["controller_code"] = _settings.ControllerCode,
                ["current_measurement_code"] = currentMeasurementCode ?? string.Empty
            });

            var envelopeData = response["data"] as JObject;
            var payload = envelopeData == null ? null : envelopeData["data"] as JObject;
            if (payload == null) payload = envelopeData;
            if (payload == null) return new MeasurementSessionConfig { Available = false };

            var available = payload.Value<bool?>("measurement_available") ?? false;
            if (!available) return new MeasurementSessionConfig { Available = false };

            var config = new MeasurementSessionConfig
            {
                Available = true,
                MeasurementCode = Clean((string)payload["measurement_code"]),
                ControllerCode = Clean((string)payload["controller_code"]),
                Status = Clean((string)payload["status"]),
                DesiredState = Clean((string)payload["desired_state"]),
                Revision = payload.Value<int?>("revision") ?? 1,
                PlannedStartAtUtc = ParseUtc((string)payload["planned_start_at"]),
                PlannedEndAtUtc = ParseUtc((string)payload["planned_end_at"]),
                Note = Clean((string)payload["note"])
            };

            var readers = payload["readers"] as JArray;
            if (readers != null)
            {
                foreach (var reader in readers.OfType<JObject>())
                {
                    var serial = Clean((string)reader["serial_number"]).ToUpperInvariant();
                    if (string.IsNullOrWhiteSpace(serial)) continue;
                    var readerConfig = new MeasurementReaderConfig
                    {
                        SerialNumber = serial,
                        PowerDbm = Math.Max(0, Math.Min(40, reader.Value<int?>("power_dbm") ?? 30)),
                        ReadIntervalMs = Math.Max(1, Math.Min(60000, reader.Value<int?>("read_interval_ms") ?? 200))
                    };
                    var antennaNumbers = reader["antennas"] as JArray;
                    if (antennaNumbers != null)
                    {
                        foreach (var number in antennaNumbers)
                        {
                            int antennaNo;
                            if (!int.TryParse(number.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out antennaNo) || antennaNo <= 0) continue;
                            if (!readerConfig.Antennas.Contains(antennaNo))
                                readerConfig.Antennas.Add(antennaNo);
                        }
                    }
                    if (readerConfig.Antennas.Count > 0
                        && !config.Readers.Any(x => string.Equals(x.SerialNumber, serial, StringComparison.OrdinalIgnoreCase)))
                    {
                        config.Readers.Add(readerConfig);
                    }
                }
            }
            return config;
        }

        public void PushMeasurementEvents(string measurementCode, IList<MeasurementEvent> events)
        {
            if (events == null || events.Count == 0) return;
            var items = new JArray();
            foreach (var item in events)
            {
                var payload = new JObject
                {
                    ["event_uid"] = item.EventUid,
                    ["revision"] = Math.Max(1, item.Revision),
                    ["power_dbm"] = Math.Max(0, item.PowerDbm),
                    ["read_interval_ms"] = Math.Max(1, Math.Min(60000, item.ReadIntervalMs)),
                    ["serial_number"] = item.SerialNumber,
                    ["antenna_no"] = item.AntennaNo,
                    ["tid"] = item.Tid,
                    ["read_at"] = item.ReadAtUtc.ToUniversalTime().ToString("o")
                };
                if (item.RssiDbm.HasValue) payload["rssi_dbm"] = item.RssiDbm.Value;
                items.Add(payload);
            }

            PostAuthenticated("controller/measurement/events", new JObject
            {
                ["controller_code"] = _settings.ControllerCode,
                ["measurement_code"] = measurementCode,
                ["events"] = items
            });
        }

        public void ReportMeasurementStatus(string measurementCode, string status, DateTime occurredAtUtc, string message)
        {
            var payload = new JObject
            {
                ["controller_code"] = _settings.ControllerCode,
                ["measurement_code"] = measurementCode,
                ["status"] = status,
                ["occurred_at"] = occurredAtUtc.ToUniversalTime().ToString("o")
            };
            if (!string.IsNullOrWhiteSpace(message)) payload["message"] = message.Trim();
            PostAuthenticated("controller/measurement/status", payload);
        }

        private CoreApiAuthResult AuthenticateWithDiscoveryFallback(Exception firstError)
        {
            if (_logger != null)
                _logger.Warn("core-api", "Configured Core API is unreachable; starting Zeroconf fallback", DescribeException(firstError));

            IList<NspDiscoveryResult> candidates;
            try
            {
                candidates = _discovery.Discover(_settings.DiscoveryTimeoutMs, _settings.DiscoveryServiceType);
            }
            catch (Exception discoveryError)
            {
                throw BuildConnectionException(BaseUrl, firstError, discoveryError);
            }

            if (candidates == null || candidates.Count == 0)
                throw new InvalidOperationException(
                    "Core API connection failed. Server=" + DisplayUrl(BaseUrl) +
                    ". Network error: " + DescribeException(firstError) +
                    ". Zeroconf found no NSP Core API service.");

            if (candidates.Count > 1)
                throw new InvalidOperationException(
                    "Configured Core API is unavailable and Zeroconf found multiple NSP servers (" +
                    string.Join(", ", candidates.Select(x => x.BaseUrl).ToArray()) +
                    "). Configure the intended Server URL explicitly.");

            var candidate = candidates[0];
            var result = AuthenticateAt(candidate.BaseUrl, true, candidate.AuthPath);
            if (_logger != null) _logger.Info("zeroconf", "Discovered Edge authenticated and selected", candidate.BaseUrl);
            return result;
        }

        private CoreApiAuthResult AuthenticateAt(string baseUrl, bool saveOnSuccess, string authPath = "/auth/token")
        {
            ValidateIdentitySettings();
            baseUrl = AppSettings.NormalizeBaseUrl(baseUrl);
            authPath = string.IsNullOrWhiteSpace(authPath) ? "/auth/token" : authPath.Trim();
            if (!authPath.StartsWith("/", StringComparison.Ordinal)) authPath = "/" + authPath;
            var response = PostRaw(baseUrl, authPath, new JObject
            {
                ["client_id"] = _settings.CoreApiClientId,
                ["client_secret"] = _settings.CoreApiClientSecret
            }, null);
            ApplyAuthResponse(response);
            if (saveOnSuccess || !string.Equals(BaseUrl, baseUrl, StringComparison.OrdinalIgnoreCase))
                _settings.SaveDiscoveredBaseUrl(baseUrl);
            if (_logger != null) _logger.Info("auth", "Core API authenticated", "server=" + baseUrl);
            return CurrentAuthResult("authenticated");
        }

        private CoreApiAuthResult RefreshCurrentServer()
        {
            var response = PostRaw(BaseUrl, "/auth/refresh", new JObject
            {
                ["refresh_token"] = _refreshToken
            }, null);
            ApplyAuthResponse(response);
            if (_logger != null) _logger.Info("auth", "Core API token refreshed");
            return CurrentAuthResult("refreshed");
        }

        private void ApplyAuthResponse(JObject response)
        {
            var data = response["data"] as JObject;
            if (data == null) throw new InvalidOperationException("Authentication response does not contain data.");

            var accessToken = Clean((string)data["access_token"]);
            var refreshToken = Clean((string)data["refresh_token"]);
            if (string.IsNullOrWhiteSpace(accessToken))
                throw new InvalidOperationException("Authentication response does not contain data.access_token.");

            var expiresIn = data.Value<int?>("expires_in") ?? 86400;
            var refreshExpiresIn = data.Value<int?>("refresh_expires_in") ?? 0;
            _accessToken = accessToken;
            _refreshToken = refreshToken;
            _expiresAtUtc = DateTime.UtcNow.AddSeconds(Math.Max(60, expiresIn));
            _refreshExpiresAtUtc = refreshExpiresIn > 0 ? (DateTime?)DateTime.UtcNow.AddSeconds(refreshExpiresIn) : null;
        }

        private JObject PostAuthenticated(string suffix, JObject payload)
        {
            EnsureAuthenticated();
            try
            {
                return PostRaw(BaseUrl, "/v1/" + (suffix ?? string.Empty).Trim('/'), payload, _accessToken);
            }
            catch (CoreApiHttpException ex)
            {
                if (ex.StatusCode != 401) throw;
                lock (_authGate)
                {
                    _accessToken = null;
                    _expiresAtUtc = null;
                }
                EnsureAuthenticated();
                return PostRaw(BaseUrl, "/v1/" + (suffix ?? string.Empty).Trim('/'), payload, _accessToken);
            }
            catch (Exception ex)
            {
                if (!_settings.DiscoveryEnabled || !IsConnectivityFailure(ex)) throw;
                lock (_authGate)
                {
                    _accessToken = null;
                    _expiresAtUtc = null;
                }
                EnsureAuthenticated();
                return PostRaw(BaseUrl, "/v1/" + (suffix ?? string.Empty).Trim('/'), payload, _accessToken);
            }
        }

        private JObject PostRaw(string baseUrl, string path, JObject payload, string bearerToken)
        {
            var url = BuildUrl(baseUrl, path);
            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                request.Content = new StringContent((payload ?? new JObject()).ToString(Formatting.None), Encoding.UTF8, "application/json");
                if (!string.IsNullOrWhiteSpace(bearerToken)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

                HttpResponseMessage response;
                string body;
                try
                {
                    response = _http.SendAsync(request).GetAwaiter().GetResult();
                    body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    throw new HttpRequestException("Cannot connect to " + url + ": " + DescribeException(ex), ex);
                }

                JObject json;
                try { json = string.IsNullOrWhiteSpace(body) ? new JObject() : JObject.Parse(body); }
                catch { json = new JObject { ["raw"] = body ?? string.Empty }; }
                if (json["result"] is JObject) json = (JObject)json["result"];

                if (!response.IsSuccessStatusCode)
                {
                    var message = Clean((string)json["message"] ?? (string)json["error"] ?? response.ReasonPhrase) ?? "request failed";
                    throw new CoreApiHttpException((int)response.StatusCode, path, message);
                }
                return json;
            }
        }

        private string BuildUrl(string baseUrl, string path)
        {
            baseUrl = AppSettings.NormalizeBaseUrl(baseUrl);
            if (string.IsNullOrWhiteSpace(baseUrl)) throw new InvalidOperationException("Core API Server URL is required.");
            var url = baseUrl + (path.StartsWith("/") ? path : "/" + path);
            if (!string.IsNullOrWhiteSpace(_settings.CoreApiDatabase))
                url += (url.Contains("?") ? "&" : "?") + "db=" + Uri.EscapeDataString(_settings.CoreApiDatabase);
            return url;
        }

        private bool IsTokenUsable()
        {
            return !string.IsNullOrWhiteSpace(_accessToken) &&
                   (!_expiresAtUtc.HasValue || _expiresAtUtc.Value > DateTime.UtcNow.AddMinutes(1));
        }

        private CoreApiAuthResult CurrentAuthResult(string message)
        {
            return new CoreApiAuthResult
            {
                Success = IsTokenUsable(),
                AccessToken = _accessToken,
                ExpiresAtUtc = _expiresAtUtc,
                Message = message
            };
        }

        private void ValidateIdentitySettings()
        {
            if (string.IsNullOrWhiteSpace(_settings.ControllerCode)) throw new InvalidOperationException("Controller Code is required.");
            if (string.IsNullOrWhiteSpace(_settings.CoreApiClientId)) throw new InvalidOperationException("Core API Client ID is required.");
            if (string.IsNullOrWhiteSpace(_settings.CoreApiClientSecret)) throw new InvalidOperationException("Core API Client Secret is required.");
        }

        private static bool IsAuthorizationFailure(Exception error)
        {
            var http = error as CoreApiHttpException;
            return http != null && (http.StatusCode == 401 || http.StatusCode == 403);
        }

        private static bool IsConnectivityFailure(Exception error)
        {
            for (var ex = error; ex != null; ex = ex.InnerException)
            {
                if (ex is HttpRequestException || ex is TaskCanceledException || ex is WebException || ex is SocketException || ex is TimeoutException)
                    return true;
            }
            return false;
        }

        private static Exception BuildConnectionException(string baseUrl, Exception networkError, Exception discoveryError)
        {
            var message = "Core API connection failed. Server=" + DisplayUrl(baseUrl) + ". " + DescribeException(networkError);
            if (discoveryError != null) message += " Zeroconf: " + DescribeException(discoveryError);
            return new InvalidOperationException(message, networkError);
        }

        private static string DescribeException(Exception error)
        {
            if (error == null) return "unknown error";
            var parts = new List<string>();
            for (var ex = error; ex != null && parts.Count < 5; ex = ex.InnerException)
            {
                var text = (ex.Message ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(text) && !parts.Contains(text)) parts.Add(text);
            }
            return string.Join(" -> ", parts.ToArray());
        }

        private static string DisplayUrl(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "<empty>" : value;
        }

        private static string DeviceStatusName(ReaderStatus status)
        {
            if (status == null) return "offline";
            if (status.Online) return "online";
            var message = (status.Message ?? string.Empty).ToLowerInvariant();
            if (message.Contains("connecting") || message.Contains("error") || message.Contains("fail") || message.Contains("timeout"))
                return "degraded";
            return "offline";
        }

        private static DateTime? ParseUtc(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            DateTimeOffset dto;
            if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out dto))
                return dto.UtcDateTime;
            return null;
        }

        private static string Sha256(string text)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? string.Empty));
                var builder = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes) builder.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return builder.ToString();
            }
        }

        private static string Clean(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        public void Dispose()
        {
            _http.Dispose();
        }

        private sealed class CoreApiHttpException : InvalidOperationException
        {
            public int StatusCode { get; private set; }

            public CoreApiHttpException(int statusCode, string path, string message)
                : base("Core API " + path + " failed: HTTP " + statusCode + " - " + message)
            {
                StatusCode = statusCode;
            }
        }
    }
}
