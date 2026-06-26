using System.IO;
using System.Globalization;

namespace DtsMonitor.App.Services;

public sealed class AppLogger
{
    private readonly string _logPath;
    private readonly object _sync = new();

    public AppLogger(string logPath)
    {
        _logPath = logPath;
        string? dir = Path.GetDirectoryName(_logPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    public void Info(string message) => Write("INFO", message);
    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message)
    {
        string line = $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)} [{level}] {message}{Environment.NewLine}";
        lock (_sync)
        {
            File.AppendAllText(_logPath, line);
        }
    }
}
