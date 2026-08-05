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
        private const string LaneCalibrationPullRoute = "controller/lane-calibrations/pull";
        private const string LaneCalibrationEventsRoute = "controller/lane-calibrations/events";
        private const string LaneCalibrationStatusRoute = "controller/lane-calibrations/status";
        private readonly AppSettings _settings;
        private readonly FileLogger _logger;
        private readonly ZeroconfDiscoveryClient _discovery;
        private readonly HttpClient _http;
        private readonly object _authGate = new object();
        private readonly object _rateGate = new object();
        private DateTime? _serverRateLimitedUntilUtc;
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

        public void EnsureAuthenticated()
        {
            lock (_authGate)
            {
                if (IsTokenUsable()) return;
                ValidateIdentitySettings();

                if (!string.IsNullOrWhiteSpace(_refreshToken) &&
                    (!_refreshExpiresAtUtc.HasValue || _refreshExpiresAtUtc.Value > DateTime.UtcNow.AddMinutes(1)) &&
                    !string.IsNullOrWhiteSpace(BaseUrl))
                {
                    try
                    {
                        RefreshCurrentServer();
                        return;
                    }
                    catch (Exception refreshError)
                    {
                        if (_logger != null) _logger.Warn("auth", "Refresh token failed", DescribeException(refreshError));
                        _refreshToken = null;
                        _refreshExpiresAtUtc = null;

                        if (IsConnectivityFailure(refreshError))
                        {
                            AuthenticateWithDiscoveryFallback(refreshError);
                            return;
                        }
                        if (!IsAuthorizationFailure(refreshError)) throw;
                    }
                }

                if (!string.IsNullOrWhiteSpace(BaseUrl))
                {
                    try
                    {
                        AuthenticateAt(BaseUrl, false);
                        return;
                    }
                    catch (Exception firstError)
                    {
                        if (!IsConnectivityFailure(firstError)) throw;
                        if (!_settings.DiscoveryEnabled) throw BuildConnectionException(BaseUrl, firstError, null);
                        AuthenticateWithDiscoveryFallback(firstError);
                        return;
                    }
                }

                if (!_settings.DiscoveryEnabled)
                    throw new InvalidOperationException("Core API Server URL is empty and Zeroconf fallback is disabled.");

                AuthenticateWithDiscoveryFallback(new InvalidOperationException("No saved Core API Server URL."));
            }
        }

        public bool IsPermanentRequestError(Exception error)
        {
            var http = error as CoreApiHttpException;
            return http != null && http.StatusCode >= 400 && http.StatusCode < 500 &&
                   http.StatusCode != 401 && http.StatusCode != 408 && http.StatusCode != 429;
        }

        public bool IsRateLimitError(Exception error)
        {
            var http = error as CoreApiHttpException;
            return error is CoreApiClientThrottleException || (http != null && http.StatusCode == 429);
        }

        public TimeSpan? GetRateLimitRetryDelay(Exception error)
        {
            var local = error as CoreApiClientThrottleException;
            if (local != null) return NormalizeRetryDelay(local.RetryAfter);

            var http = error as CoreApiHttpException;
            if (http != null && http.StatusCode == 429)
                return NormalizeRetryDelay(http.RetryAfter ?? TimeSpan.FromSeconds(_settings.CoreApiRateLimitBackoffSec));

            return null;
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
            ResetRateState();
        }

        public JObject Heartbeat()
        {
            return PostAuthenticated("heartbeat", new JObject { ["controller_code"] = _settings.ControllerCode });
        }

        public ControllerRuntimeConfigurationSnapshot PullControllerRuntimeConfiguration()
        {
            var response = PostAuthenticated("controller/device-config/pull", new JObject
            {
                ["controller_code"] = _settings.ControllerCode
            });

            var snapshot = new ControllerRuntimeConfigurationSnapshot();
            var data = ExtractBusinessData(response, "Controller runtime configuration");
            if (data == null) return snapshot;

            var devices = data["devices"] as JArray;
            var seenSerials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var token in (devices ?? new JArray()).OfType<JObject>())
            {
                var serial = Clean((string)token["serial_number"]);
                if (string.IsNullOrWhiteSpace(serial))
                {
                    if (_logger != null) _logger.Warn("device-config", "Ignored Reader config without serial_number", token.ToString(Formatting.None));
                    continue;
                }
                serial = NormalizeSerial(serial);
                if (!seenSerials.Add(serial))
                {
                    if (_logger != null) _logger.Warn("device-config", "Duplicate Reader runtime parameters ignored", "serial=" + serial);
                    continue;
                }

                var readerParameters = token["reader_parameters"] as JObject ?? new JObject();
                var config = new ReaderDeviceConfig
                {
                    SerialNumber = serial,
                    Enabled = true,
                    PowerDbm = Clamp(readerParameters.Value<int?>("power_dbm") ?? 30, 0, 33),
                    ReadIntervalMs = Clamp(readerParameters.Value<int?>("read_interval_ms") ?? 200, 1, 60000),
                    TidStartAddress = Math.Max(0, readerParameters.Value<int?>("tid_start_address") ?? 2),
                    TidLength = Math.Max(1, readerParameters.Value<int?>("tid_length") ?? 4)
                };
                config.ConfigHash = ReaderRuntimeConfigHash(config);

                // Server owns Reader Port topology. Controller ignores token["ports"]
                // and forwards the raw port_no reported by the Reader SDK.
                snapshot.Devices.Add(config);
            }

            var layouts = data["parking_layouts"] as JArray;
            var seenLayouts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var token in (layouts ?? new JArray()).OfType<JObject>())
            {
                var code = Clean((string)token["parking_area_code"]);
                if (string.IsNullOrWhiteSpace(code) || !seenLayouts.Add(code)) continue;
                var layout = new ParkingLayoutRuntimeInfo
                {
                    Code = code.ToUpperInvariant(),
                    Name = Clean((string)token["parking_area_name"]),
                    State = Clean((string)token["state"]),
                    PublishedRevision = Math.Max(0, token.Value<int?>("published_revision") ?? 0),
                };
                var lanes = token["lanes"] as JArray;
                foreach (var laneToken in (lanes ?? new JArray()).OfType<JObject>())
                {
                    var laneCode = Clean((string)laneToken["lane_code"]);
                    if (string.IsNullOrWhiteSpace(laneCode)) continue;
                    layout.Lanes.Add(new ParkingLaneRuntimeInfo
                    {
                        Code = laneCode.ToUpperInvariant(),
                        Name = Clean((string)laneToken["lane_name"]),
                    });
                }
                snapshot.ParkingLayouts.Add(layout);
            }

            return snapshot;
        }

        public void ReportReaderStatus(IList<ReaderStatus> statuses)
        {
            var devices = new JArray();
            foreach (var status in statuses ?? new List<ReaderStatus>())
            {
                if (status == null || string.IsNullOrWhiteSpace(status.SerialNumber)) continue;
                var reportedSeenAt = status.Online ? DateTime.UtcNow : status.UpdatedAtUtc;
                devices.Add(new JObject
                {
                    ["serial_number"] = status.SerialNumber.Trim().ToUpperInvariant(),
                    ["endpoint"] = status.Endpoint ?? string.Empty,
                    ["status"] = DeviceStatusName(status),
                    ["last_seen_at"] = reportedSeenAt == default(DateTime) ? null : reportedSeenAt.ToUniversalTime().ToString("o"),
                    ["firmware_version"] = status.FirmwareVersion ?? string.Empty,
                    ["power_dbm"] = Math.Max(0, Math.Min(40, status.PowerDbm)),
                    ["read_interval_ms"] = Math.Max(1, Math.Min(60000, status.ReadIntervalMs)),
                    ["ports"] = new JArray((status.Ports ?? new List<int>()).Where(value => value >= 1 && value <= 16).Distinct().OrderBy(value => value)),
                });
            }

            var response = PostAuthenticated("devices/report", new JObject
            {
                ["controller_code"] = _settings.ControllerCode,
                ["devices"] = devices,
            });
            LogBatchFailures("reader-observation", response);
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
                    ["serial_number"] = detection.SerialNumber,
                    ["port_no"] = detection.PortNo,
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

        public LaneCalibrationSessionConfig PullLaneCalibration(string currentLaneCalibrationCode)
        {
            var response = PostAuthenticated(LaneCalibrationPullRoute, new JObject
            {
                ["controller_code"] = _settings.ControllerCode,
                ["current_lane_calibration_code"] = currentLaneCalibrationCode ?? string.Empty
            });

            var payload = ExtractBusinessData(response, "Lane Calibration pull");
            if (payload == null) return new LaneCalibrationSessionConfig { Available = false };

            var available = payload.Value<bool?>("lane_calibration_available") ?? false;
            if (!available)
                return new LaneCalibrationSessionConfig
                {
                    Available = false,
                    Reason = Clean((string)payload["reason"]),
                };

            var config = new LaneCalibrationSessionConfig
            {
                Available = true,
                LaneCalibrationCode = Clean((string)payload["lane_calibration_code"]),
                Status = Clean((string)payload["status"]),
                DesiredState = Clean((string)payload["desired_state"]),
                Revision = payload.Value<int?>("revision") ?? 1
            };

            if (string.IsNullOrWhiteSpace(config.LaneCalibrationCode))
                throw new InvalidOperationException("Lane Calibration response requires data.lane_calibration_code when data.lane_calibration_available is true.");

            var readers = payload["readers"] as JArray;
            if (readers != null)
            {
                foreach (var reader in readers.OfType<JObject>())
                {
                    var serial = NormalizeSerial((string)reader["serial_number"]);
                    if (string.IsNullOrWhiteSpace(serial)) continue;
                    var readerConfig = new LaneCalibrationReaderConfig
                    {
                        SerialNumber = serial,
                        PowerDbm = Math.Max(0, Math.Min(40, reader.Value<int?>("power_dbm") ?? 30)),
                        ReadIntervalMs = Math.Max(1, Math.Min(60000, reader.Value<int?>("read_interval_ms") ?? 200))
                    };
                    if (!config.Readers.Any(x => string.Equals(x.SerialNumber, serial, StringComparison.OrdinalIgnoreCase)))
                        config.Readers.Add(readerConfig);
                }
            }
            return config;
        }

        public LaneCalibrationPushAck PushLaneCalibrationEvents(string laneCalibrationCode, IList<LaneCalibrationEvent> events)
        {
            if (events == null || events.Count == 0)
                return new LaneCalibrationPushAck { LaneCalibrationCode = laneCalibrationCode };
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
                    ["port_no"] = item.PortNo,
                    ["tid"] = item.Tid,
                    ["read_at"] = item.ReadAtUtc.ToUniversalTime().ToString("o")
                };
                if (item.RssiDbm.HasValue) payload["rssi_dbm"] = item.RssiDbm.Value;
                items.Add(payload);
            }

            var response = PostAuthenticated(LaneCalibrationEventsRoute, new JObject
            {
                ["controller_code"] = _settings.ControllerCode,
                ["lane_calibration_code"] = laneCalibrationCode,
                ["events"] = items
            });

            var data = ExtractBusinessData(response, "Lane Calibration Edge acknowledgement");
            if (data == null || data["received"] == null)
            {
                // Backward compatibility with an older Edge that returned only
                // a transport-level HTTP 200 acknowledgement.
                return new LaneCalibrationPushAck
                {
                    LaneCalibrationCode = laneCalibrationCode,
                    Received = events.Count,
                    Stored = -1,
                    Duplicates = -1,
                    Ignored = -1,
                    Rejected = -1,
                };
            }

            var ack = new LaneCalibrationPushAck
            {
                LaneCalibrationCode = Clean((string)data["lane_calibration_code"]) ?? laneCalibrationCode,
                Received = data.Value<int?>("received") ?? -1,
                Stored = data.Value<int?>("stored") ?? -1,
                Duplicates = data.Value<int?>("duplicates") ?? 0,
                Ignored = data.Value<int?>("ignored") ?? 0,
                Rejected = data.Value<int?>("rejected") ?? 0,
            };
            if (ack.Received >= 0 && ack.Received != events.Count)
                throw new InvalidOperationException(
                    "Lane Calibration Edge acknowledgement count mismatch. sent="
                    + events.Count.ToString(CultureInfo.InvariantCulture)
                    + "; received=" + ack.Received.ToString(CultureInfo.InvariantCulture));
            return ack;
        }

        public void ReportLaneCalibrationStatus(string laneCalibrationCode, string status, DateTime occurredAtUtc, string message)
        {
            var payload = new JObject
            {
                ["controller_code"] = _settings.ControllerCode,
                ["lane_calibration_code"] = laneCalibrationCode,
                ["status"] = status,
                ["occurred_at"] = occurredAtUtc.ToUniversalTime().ToString("o")
            };
            if (!string.IsNullOrWhiteSpace(message)) payload["message"] = message.Trim();
            PostAuthenticated(LaneCalibrationStatusRoute, payload);
        }

        private void AuthenticateWithDiscoveryFallback(Exception firstError)
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
            AuthenticateAt(candidate.BaseUrl, true, candidate.AuthPath);
            if (_logger != null) _logger.Info("zeroconf", "Discovered Edge authenticated and selected", candidate.BaseUrl);
        }

        private void AuthenticateAt(string baseUrl, bool saveOnSuccess, string authPath = "/auth/token")
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
        }

        private void RefreshCurrentServer()
        {
            var response = PostRaw(BaseUrl, "/auth/refresh", new JObject
            {
                ["refresh_token"] = _refreshToken
            }, null);
            ApplyAuthResponse(response);
            if (_logger != null) _logger.Info("auth", "Core API token refreshed");
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
            ResetRateState();
        }

        private JObject PostAuthenticated(string suffix, JObject payload)
        {
            EnsureAuthenticated();
            var path = "/v1/" + (suffix ?? string.Empty).Trim('/');
            try
            {
                return SendAuthenticatedRequest(path, payload);
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
                return SendAuthenticatedRequest(path, payload);
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
                return SendAuthenticatedRequest(path, payload);
            }
        }

        private JObject SendAuthenticatedRequest(string path, JObject payload)
        {
            HonorServerRateLimitCooldown();
            try
            {
                return PostRaw(BaseUrl, path, payload, _accessToken);
            }
            catch (CoreApiHttpException ex)
            {
                if (ex.StatusCode == 429) RegisterServerRateLimit(ex.RetryAfter);
                throw;
            }
        }

        private void HonorServerRateLimitCooldown()
        {
            // Controller does not impose a client-side requests-per-minute quota.
            // Server policy is authoritative. This method only honors a cooldown
            // explicitly returned by the server through HTTP 429 / Retry-After.
            var now = DateTime.UtcNow;
            lock (_rateGate)
            {
                if (!_serverRateLimitedUntilUtc.HasValue) return;
                if (_serverRateLimitedUntilUtc.Value > now)
                    throw new CoreApiClientThrottleException(
                        _serverRateLimitedUntilUtc.Value - now,
                        "Server rate-limit cooldown is active.");
                _serverRateLimitedUntilUtc = null;
            }
        }

        private void ResetRateState()
        {
            lock (_rateGate)
            {
                _serverRateLimitedUntilUtc = null;
            }
        }

        private void RegisterServerRateLimit(TimeSpan? retryAfter)
        {
            var delay = NormalizeRetryDelay(retryAfter ?? TimeSpan.FromSeconds(_settings.CoreApiRateLimitBackoffSec));
            var until = DateTime.UtcNow.Add(delay);
            lock (_rateGate)
            {
                if (!_serverRateLimitedUntilUtc.HasValue || _serverRateLimitedUntilUtc.Value < until)
                    _serverRateLimitedUntilUtc = until;
            }
            if (_logger != null)
                _logger.Warn("core-api", "Server rate limit reached; all API workers paused",
                    "retry_in_sec=" + Math.Ceiling(delay.TotalSeconds).ToString(CultureInfo.InvariantCulture));
        }

        private static TimeSpan NormalizeRetryDelay(TimeSpan value)
        {
            if (value < TimeSpan.FromMilliseconds(250)) return TimeSpan.FromMilliseconds(250);
            if (value > TimeSpan.FromMinutes(5)) return TimeSpan.FromMinutes(5);
            return value;
        }

        private JObject PostRaw(string baseUrl, string path, JObject payload, string bearerToken)
        {
            var url = BuildUrl(baseUrl, path);
            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                request.Content = new StringContent((payload ?? new JObject()).ToString(Formatting.None), Encoding.UTF8, "application/json");
                if (!string.IsNullOrWhiteSpace(bearerToken)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

                try
                {
                    using (var response = _http.SendAsync(request).GetAwaiter().GetResult())
                    {
                        var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                        JObject json;
                        try { json = string.IsNullOrWhiteSpace(body) ? new JObject() : JObject.Parse(body); }
                        catch { json = new JObject { ["raw"] = body ?? string.Empty }; }
                        if (json["result"] is JObject) json = (JObject)json["result"];

                        if (!response.IsSuccessStatusCode)
                        {
                            var message = Clean((string)json["message"] ?? (string)json["error"] ?? response.ReasonPhrase) ?? "request failed";
                            throw new CoreApiHttpException((int)response.StatusCode, path, message, ParseRetryAfter(response));
                        }
                        return json;
                    }
                }
                catch (CoreApiHttpException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    throw new HttpRequestException("Cannot connect to " + url + ": " + DescribeException(ex), ex);
                }
            }
        }

        private static TimeSpan? ParseRetryAfter(HttpResponseMessage response)
        {
            if (response == null || response.Headers == null || response.Headers.RetryAfter == null) return null;
            var value = response.Headers.RetryAfter;
            if (value.Delta.HasValue) return NormalizeRetryDelay(value.Delta.Value);
            if (value.Date.HasValue) return NormalizeRetryDelay(value.Date.Value.UtcDateTime - DateTime.UtcNow);
            return null;
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

        private void LogBatchFailures(string category, JObject response)
        {
            if (_logger == null || response == null) return;
            var data = ExtractBusinessData(response, category + " acknowledgement");
            var failed = data == null ? 0 : data.Value<int?>("failed") ?? 0;
            if (failed > 0)
                _logger.Warn(category, "Server rejected one or more batch items",
                    "failed=" + failed.ToString(CultureInfo.InvariantCulture));
        }


        private static JObject ExtractBusinessData(JObject response, string operation)
        {
            if (response == null) return null;

            var current = response;
            for (var depth = 0; depth < 4 && current != null; depth++)
            {
                var success = current.Value<bool?>("success");
                if (success.HasValue && !success.Value)
                {
                    var message = Clean((string)current["message"])
                        ?? Clean((string)current["error"])
                        ?? "request failed";
                    throw new InvalidOperationException(operation + " reported success=false: " + message);
                }

                var result = current["result"] as JObject;
                if (result != null)
                {
                    current = result;
                    continue;
                }

                var data = current["data"] as JObject;
                if (data == null) return current;

                // T4 Core API deployments may expose either transport data directly
                // or a canonical { success, data } envelope inside transport data.
                // Continue unwrapping both forms until the actual business payload.
                current = data;
            }
            return current;
        }

        private static string ReaderRuntimeConfigHash(ReaderDeviceConfig config)
        {
            if (config == null) return Sha256(string.Empty);
            var runtime = new JObject
            {
                ["serial_number"] = config.SerialNumber ?? string.Empty,
                ["power_dbm"] = config.PowerDbm,
                ["read_interval_ms"] = config.ReadIntervalMs,
                ["tid_start_address"] = config.TidStartAddress,
                ["tid_length"] = config.TidLength
            };
            return Sha256(runtime.ToString(Formatting.None));
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

        private static int Clamp(int value, int min, int max)
        {
            return Math.Max(min, Math.Min(max, value));
        }

        private static string NormalizeSerial(string value)
        {
            var normalized = Clean(value);
            if (normalized == null) return null;
            var text = normalized.ToUpperInvariant();
            if (text.StartsWith("0X", StringComparison.Ordinal)) text = text.Substring(2);
            var compact = new string(text.Where(Uri.IsHexDigit).ToArray());
            return compact.Length == 8 ? compact : text;
        }


        private static string Clean(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        public void Dispose()
        {
            _http.Dispose();
        }

        private sealed class CoreApiClientThrottleException : InvalidOperationException
        {
            public TimeSpan RetryAfter { get; private set; }

            public CoreApiClientThrottleException(TimeSpan retryAfter, string message)
                : base(message + " Retry after " + Math.Ceiling(retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture) + " second(s).")
            {
                RetryAfter = retryAfter;
            }
        }

        private sealed class CoreApiHttpException : InvalidOperationException
        {
            public int StatusCode { get; private set; }
            public TimeSpan? RetryAfter { get; private set; }

            public CoreApiHttpException(int statusCode, string path, string message, TimeSpan? retryAfter)
                : base("Core API " + path + " failed: HTTP " + statusCode + " - " + message)
            {
                StatusCode = statusCode;
                RetryAfter = retryAfter;
            }
        }
    }
}
