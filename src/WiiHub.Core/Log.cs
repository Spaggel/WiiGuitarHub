namespace WiiHub.Core;

public enum LogLevel { Info, Warning, Error, Debug }

public sealed record LogEntry(DateTime Time, LogLevel Level, string Message)
{
    public override string ToString() => $"{Time:HH:mm:ss} {Level,-7} {Message}";
}

/// <summary>Tiny thread-safe in-memory log the UI can display.</summary>
public static class Log
{
    private const int Capacity = 500;
    private static readonly object Sync = new();
    private static readonly Queue<LogEntry> Entries = new();

    /// <summary>Raised on the thread that logged; subscribers must marshal to their UI thread.</summary>
    public static event Action<LogEntry>? EntryAdded;

    public static bool IncludeDebug { get; set; }

    public static void Info(string message) => Write(LogLevel.Info, message);
    public static void Warn(string message) => Write(LogLevel.Warning, message);
    public static void Error(string message) => Write(LogLevel.Error, message);
    public static void Debug(string message) => Write(LogLevel.Debug, message);

    public static IReadOnlyList<LogEntry> Snapshot()
    {
        lock (Sync) return Entries.ToArray();
    }

    private static void Write(LogLevel level, string message)
    {
        if (level == LogLevel.Debug && !IncludeDebug) return;
        var entry = new LogEntry(DateTime.Now, level, message);
        lock (Sync)
        {
            Entries.Enqueue(entry);
            while (Entries.Count > Capacity) Entries.Dequeue();
        }
        System.Diagnostics.Debug.WriteLine(entry.ToString());
        EntryAdded?.Invoke(entry);
    }
}
