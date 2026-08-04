using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace NSPGatekeeper.Controller.Infrastructure.Logging
{
    public sealed class LogEntry
    {
        public DateTime AtLocal { get; set; }
        public string Level { get; set; }
        public string Area { get; set; }
        public string Message { get; set; }
        public string Detail { get; set; }
        public int ProcessId { get; set; }
        public int ThreadId { get; set; }

        public override string ToString()
        {
            return AtLocal.ToString("yyyy-MM-dd HH:mm:ss.fff")
                + " [" + Level + "]"
                + " [" + Area + "]"
                + " [P" + ProcessId + "/T" + ThreadId + "] "
                + Message
                + (string.IsNullOrWhiteSpace(Detail) ? string.Empty : " | " + Detail);
        }
    }

    public sealed class FileLogger
    {
        private const int RecentEntryLimit = 1000;

        private readonly string _directory;
        private readonly object _gate = new object();
        private readonly Queue<LogEntry> _recent = new Queue<LogEntry>();

        public event Action<LogEntry> EntryWritten;

        public FileLogger(string directory)
        {
            _directory = Path.GetFullPath(string.IsNullOrWhiteSpace(directory) ? "logs" : directory);
            Directory.CreateDirectory(_directory);
        }

        public string DirectoryPath { get { return _directory; } }

        public void Info(string area, string message, string detail = null)
        {
            Write("INFO", area, message, detail);
        }

        public void Warn(string area, string message, string detail = null)
        {
            Write("WARN", area, message, detail);
        }

        public void Error(string area, string message, Exception ex, string context = null)
        {
            var exceptionDetail = FormatException(ex);
            var detail = string.IsNullOrWhiteSpace(context)
                ? exceptionDetail
                : context + (string.IsNullOrWhiteSpace(exceptionDetail) ? string.Empty : Environment.NewLine + exceptionDetail);
            Write("ERROR", area, message, detail);
        }

        public IList<LogEntry> Snapshot(int maxEntries)
        {
            lock (_gate)
            {
                var count = Math.Max(1, Math.Min(RecentEntryLimit, maxEntries));
                return _recent.Skip(Math.Max(0, _recent.Count - count)).ToList();
            }
        }

        private void Write(string level, string area, string message, string detail)
        {
            var entry = new LogEntry
            {
                AtLocal = DateTime.Now,
                Level = string.IsNullOrWhiteSpace(level) ? "INFO" : level.Trim().ToUpperInvariant(),
                Area = string.IsNullOrWhiteSpace(area) ? "app" : area.Trim(),
                Message = message ?? string.Empty,
                Detail = detail,
                ProcessId = Process.GetCurrentProcess().Id,
                ThreadId = Thread.CurrentThread.ManagedThreadId
            };

            lock (_gate)
            {
                _recent.Enqueue(entry);
                while (_recent.Count > RecentEntryLimit) _recent.Dequeue();

                try
                {
                    var path = Path.Combine(_directory, DateTime.Now.ToString("yyyy-MM-dd") + ".log");
                    File.AppendAllText(path, entry + Environment.NewLine, Encoding.UTF8);
                }
                catch (Exception fileError)
                {
                    Debug.WriteLine("File logger write failed: " + fileError);
                    Debug.WriteLine(entry.ToString());
                }
            }

            var handler = EntryWritten;
            if (handler == null) return;

            try
            {
                handler(entry);
            }
            catch (Exception subscriberError)
            {
                Debug.WriteLine("Log subscriber failed: " + subscriberError);
            }
        }

        private static string FormatException(Exception ex)
        {
            if (ex == null) return null;

            var parts = new List<string>();
            var current = ex;
            var depth = 0;
            while (current != null && depth < 8)
            {
                parts.Add(
                    "exception_type=" + current.GetType().FullName
                    + "; hresult=0x" + unchecked((uint)current.HResult).ToString("X8")
                    + "; message=" + current.Message);
                current = current.InnerException;
                depth++;
            }

            if (!string.IsNullOrWhiteSpace(ex.StackTrace))
                parts.Add("stack=" + ex.StackTrace);

            return string.Join(Environment.NewLine, parts);
        }
    }
}
