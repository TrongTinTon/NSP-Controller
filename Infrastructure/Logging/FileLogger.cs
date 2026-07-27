using System;
using System.IO;
using System.Text;

namespace NSPGatekeeper.Controller.Infrastructure.Logging
{
    public sealed class LogEntry
    {
        public DateTime AtLocal { get; set; }
        public string Level { get; set; }
        public string Area { get; set; }
        public string Message { get; set; }
        public string Detail { get; set; }

        public override string ToString()
        {
            return AtLocal.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + Level + "] [" + Area + "] " + Message
                + (string.IsNullOrWhiteSpace(Detail) ? string.Empty : " | " + Detail);
        }
    }

    public sealed class FileLogger
    {
        private readonly string _directory;
        private readonly object _gate = new object();

        public event Action<LogEntry> EntryWritten;

        public FileLogger(string directory)
        {
            _directory = Path.GetFullPath(string.IsNullOrWhiteSpace(directory) ? "logs" : directory);
            Directory.CreateDirectory(_directory);
        }

        public void Info(string area, string message, string detail = null) { Write("INFO", area, message, detail); }
        public void Warn(string area, string message, string detail = null) { Write("WARN", area, message, detail); }
        public void Error(string area, string message, Exception ex)
        {
            Write("ERROR", area, message, ex == null ? null : ex.ToString());
        }

        private void Write(string level, string area, string message, string detail)
        {
            var entry = new LogEntry
            {
                AtLocal = DateTime.Now,
                Level = level,
                Area = string.IsNullOrWhiteSpace(area) ? "app" : area.Trim(),
                Message = message ?? string.Empty,
                Detail = detail
            };

            lock (_gate)
            {
                var path = Path.Combine(_directory, DateTime.Now.ToString("yyyy-MM-dd") + ".log");
                File.AppendAllText(path, entry + Environment.NewLine, Encoding.UTF8);
            }

            var handler = EntryWritten;
            if (handler != null)
            {
                try { handler(entry); } catch { }
            }
        }
    }
}
