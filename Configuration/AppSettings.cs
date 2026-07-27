using System;
using System.Configuration;

namespace NSPGatekeeper.Controller.Configuration
{
    public sealed class AppSettings
    {
        public string ControllerCode { get; set; }
        public string ControllerName { get; set; }
        public string CoreApiBaseUrl { get; set; }
        public string CoreApiClientId { get; set; }
        public string CoreApiClientSecret { get; set; }
        public string CoreApiDatabase { get; set; }
        public int CoreApiTimeoutSec { get; set; }
        public bool DiscoveryEnabled { get; set; }
        public string DiscoveryServiceType { get; set; }
        public int DiscoveryTimeoutMs { get; set; }
        public int HeartbeatIntervalSec { get; set; }
        public int DeviceConfigIntervalSec { get; set; }
        public int DeviceStatusIntervalSec { get; set; }
        public int DetectionPushIntervalMs { get; set; }
        public int DetectionBatchSize { get; set; }
        public int MeasurementPollIntervalSec { get; set; }
        public int MeasurementPushIntervalMs { get; set; }
        public int MeasurementBatchSize { get; set; }
        public int CleanupIntervalSec { get; set; }
        public int SentDetectionRetentionDays { get; set; }
        public string PostgreSqlConnectionString { get; set; }
        public string PostgreSqlAdminConnectionString { get; set; }
        public string LogDirectory { get; set; }

        public static AppSettings Load()
        {
            return new AppSettings
            {
                ControllerCode = Read("ControllerCode", string.Empty),
                ControllerName = Read("ControllerName", Environment.MachineName),
                CoreApiBaseUrl = NormalizeBaseUrl(Read("CoreApiBaseUrl", string.Empty)),
                CoreApiClientId = Read("CoreApiClientId", string.Empty),
                CoreApiClientSecret = Read("CoreApiClientSecret", string.Empty),
                CoreApiDatabase = Read("CoreApiDatabase", string.Empty),
                CoreApiTimeoutSec = ReadInt("CoreApiTimeoutSec", 15, 3, 120),
                DiscoveryEnabled = ReadBool("DiscoveryEnabled", true),
                DiscoveryServiceType = Read("DiscoveryServiceType", "_nsp._tcp.local"),
                DiscoveryTimeoutMs = ReadInt("DiscoveryTimeoutMs", 5000, 1000, 30000),
                HeartbeatIntervalSec = ReadInt("HeartbeatIntervalSec", 30, 5, 3600),
                DeviceConfigIntervalSec = ReadInt("DeviceConfigIntervalSec", 20, 5, 3600),
                DeviceStatusIntervalSec = ReadInt("DeviceStatusIntervalSec", 30, 5, 3600),
                DetectionPushIntervalMs = ReadInt("DetectionPushIntervalMs", 200, 50, 60000),
                DetectionBatchSize = ReadInt("DetectionBatchSize", 250, 1, 1000),
                MeasurementPollIntervalSec = ReadInt("MeasurementPollIntervalSec", 2, 1, 60),
                MeasurementPushIntervalMs = ReadInt("MeasurementPushIntervalMs", 200, 50, 60000),
                MeasurementBatchSize = ReadInt("MeasurementBatchSize", 100, 1, 100),
                CleanupIntervalSec = ReadInt("CleanupIntervalSec", 3600, 60, 86400),
                SentDetectionRetentionDays = ReadInt("SentDetectionRetentionDays", 7, 1, 365),
                PostgreSqlConnectionString = Read("PostgreSqlConnectionString", string.Empty),
                PostgreSqlAdminConnectionString = Read("PostgreSqlAdminConnectionString", string.Empty),
                LogDirectory = Read("LogDirectory", "logs")
            };
        }

        public void SaveConnection()
        {
            Write("ControllerCode", ControllerCode);
            Write("ControllerName", ControllerName);
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
