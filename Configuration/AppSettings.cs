using System;
using System.Configuration;

namespace NSPGatekeeper.Controller.Configuration
{
    public sealed class AppSettings
    {
        public string ControllerCode { get; set; }
        public string CoreApiBaseUrl { get; set; }
        public string CoreApiClientId { get; set; }
        public string CoreApiClientSecret { get; set; }
        public string CoreApiDatabase { get; set; }
        public int CoreApiTimeoutSec { get; set; }
        public int CoreApiRateLimitBackoffSec { get; set; }
        public bool DiscoveryEnabled { get; set; }
        public string DiscoveryServiceType { get; set; }
        public int DiscoveryTimeoutMs { get; set; }
        public int HeartbeatIntervalSec { get; set; }
        public int ReaderConfigIntervalSec { get; set; }
        public int ReaderDiscoveryIntervalSec { get; set; }
        public int ReaderStatusIntervalSec { get; set; }
        public int DetectionPushIntervalMs { get; set; }
        public int DetectionBatchSize { get; set; }
        public int LaneCalibrationIdlePollIntervalSec { get; set; }
        public int LaneCalibrationActivePollIntervalSec { get; set; }
        public int LaneCalibrationLeaseTimeoutSec { get; set; }
        public int LaneCalibrationPushIntervalMs { get; set; }
        public int LaneCalibrationBatchSize { get; set; }
        public int CleanupIntervalSec { get; set; }
        public int SentDetectionRetentionDays { get; set; }
        public string PostgreSqlConnectionString { get; set; }
        public string PostgreSqlAdminConnectionString { get; set; }
        public string LogDirectory { get; set; }

        public static AppSettings Load()
        {
            return new AppSettings
            {
                ControllerCode = ReadOverride("NSP_CONTROLLER_CODE", "ControllerCode", string.Empty),
                CoreApiBaseUrl = NormalizeBaseUrl(ReadOverride("NSP_CORE_API_BASE_URL", "CoreApiBaseUrl", string.Empty)),
                CoreApiClientId = ReadOverride("NSP_CORE_API_CLIENT_ID", "CoreApiClientId", string.Empty),
                CoreApiClientSecret = ReadOverride("NSP_CORE_API_CLIENT_SECRET", "CoreApiClientSecret", string.Empty),
                CoreApiDatabase = ReadOverride("NSP_CORE_API_DATABASE", "CoreApiDatabase", string.Empty),
                CoreApiTimeoutSec = ReadInt("CoreApiTimeoutSec", 15, 3, 120),
                CoreApiRateLimitBackoffSec = ReadInt("CoreApiRateLimitBackoffSec", 60, 5, 300),
                DiscoveryEnabled = ReadBool("DiscoveryEnabled", true),
                DiscoveryServiceType = Read("DiscoveryServiceType", "_nsp._tcp.local"),
                DiscoveryTimeoutMs = ReadInt("DiscoveryTimeoutMs", 5000, 1000, 30000),
                HeartbeatIntervalSec = ReadInt("HeartbeatIntervalSec", 30, 5, 3600),
                ReaderConfigIntervalSec = ReadInt("ReaderConfigIntervalSec", 60, 5, 3600),
                ReaderDiscoveryIntervalSec = ReadInt("ReaderDiscoveryIntervalSec", 5, 2, 300),
                ReaderStatusIntervalSec = ReadInt("ReaderStatusIntervalSec", 60, 5, 3600),
                DetectionPushIntervalMs = ReadInt("DetectionPushIntervalMs", 1000, 100, 60000),
                DetectionBatchSize = ReadInt("DetectionBatchSize", 1000, 1, 1000),
                LaneCalibrationIdlePollIntervalSec = ReadInt("LaneCalibrationIdlePollIntervalSec", 5, 2, 300),
                LaneCalibrationActivePollIntervalSec = ReadInt("LaneCalibrationActivePollIntervalSec", 3, 1, 60),
                LaneCalibrationLeaseTimeoutSec = ReadInt("LaneCalibrationLeaseTimeoutSec", 30, 5, 300),
                LaneCalibrationPushIntervalMs = ReadInt("LaneCalibrationPushIntervalMs", 1000, 100, 60000),
                LaneCalibrationBatchSize = ReadInt("LaneCalibrationBatchSize", 100, 1, 100),
                CleanupIntervalSec = ReadInt("CleanupIntervalSec", 3600, 60, 86400),
                SentDetectionRetentionDays = ReadInt("SentDetectionRetentionDays", 7, 1, 365),
                PostgreSqlConnectionString = ReadOverride("NSP_POSTGRES_CONNECTION", "PostgreSqlConnectionString", string.Empty),
                PostgreSqlAdminConnectionString = ReadOverride("NSP_POSTGRES_ADMIN_CONNECTION", "PostgreSqlAdminConnectionString", string.Empty),
                LogDirectory = Read("LogDirectory", "logs")
            };
        }

        public void SaveConnection()
        {
            Write("ControllerCode", ControllerCode);
            Write("CoreApiBaseUrl", CoreApiBaseUrl);
            Write("CoreApiClientId", CoreApiClientId);
            Write("CoreApiClientSecret", CoreApiClientSecret);
            Write("CoreApiDatabase", CoreApiDatabase);
            Write("DiscoveryEnabled", DiscoveryEnabled ? "true" : "false");
        }

        public void SaveDiscoveredBaseUrl(string baseUrl)
        {
            CoreApiBaseUrl = NormalizeBaseUrl(baseUrl);
            Write("CoreApiBaseUrl", CoreApiBaseUrl);
        }

        public static string NormalizeBaseUrl(string value)
        {
            value = (value ?? string.Empty).Trim().TrimEnd('/');
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            if (!value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                value = "http://" + value;
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                throw new InvalidOperationException("Core API Server URL is invalid: " + value);
            return value;
        }

        private static string ReadOverride(string environmentKey, string settingKey, string fallback)
        {
            var environmentValue = Environment.GetEnvironmentVariable(environmentKey);
            return string.IsNullOrWhiteSpace(environmentValue)
                ? Read(settingKey, fallback)
                : environmentValue.Trim();
        }

        private static string Read(string key, string fallback)
        {
            var value = ConfigurationManager.AppSettings[key];
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }

        private static int ReadInt(string key, int fallback, int min, int max)
        {
            int value;
            if (!int.TryParse(Read(key, fallback.ToString()), out value)) value = fallback;
            return Math.Max(min, Math.Min(max, value));
        }

        private static bool ReadBool(string key, bool fallback)
        {
            bool value;
            return bool.TryParse(Read(key, fallback.ToString()), out value) ? value : fallback;
        }

        private static void Write(string key, string value)
        {
            var config = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None);
            var settings = config.AppSettings.Settings;
            if (settings[key] == null) settings.Add(key, value ?? string.Empty);
            else settings[key].Value = value ?? string.Empty;
            config.Save(ConfigurationSaveMode.Modified);
            ConfigurationManager.RefreshSection("appSettings");
        }
    }
}
