using System.Diagnostics;

namespace WiiHub.Core.Wiimote;

public enum LinkLevel { Unknown, Poor, Fair, Good }

/// <summary>A point in time to measure from (see <see cref="LinkStats.Mark"/>).</summary>
public readonly record struct LinkMark(long Timestamp, long TotalReports);

public sealed record LinkSnapshot(
    double ReportsPerSecond,
    double PeakReportsPerSecond,
    int Dropouts,
    double LongestGapMs,
    double? AveragePingMs,
    int LostPings,
    TimeSpan ConnectedFor);

/// <summary>
/// Connection quality of one Wiimote, measured by the app itself. Windows offers no usable signal
/// strength for connected classic-Bluetooth devices, so we measure what matters for playing:
/// <list type="bullet">
/// <item>report rate (a Wiimote in continuous mode sends about 100 reports per second),</item>
/// <item>dropouts: moments where nothing arrives for <see cref="DropoutMs"/> or longer,</item>
/// <item>response time of a small memory read sent every few seconds ("ping").</item>
/// </list>
/// All methods take an optional Stopwatch timestamp so the logic can be tested.
/// </summary>
public sealed class LinkStats
{
    public const double DropoutMs = 60;

    private static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan History = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(3);

    private readonly object _lock = new();
    private readonly long _created;
    private readonly Queue<long> _recent = new();
    private readonly List<(long At, double Ms)> _gaps = [];
    private readonly List<(long At, double Ms)> _pings = []; // Ms < 0: no answer
    private long _lastReport;
    private long _suppressUntil;
    private long _totalReports;
    private double _peakRate;

    public LinkStats(long? now = null)
    {
        _created = now ?? Stopwatch.GetTimestamp();
    }

    public static long Ticks(TimeSpan t) => (long)(t.TotalSeconds * Stopwatch.Frequency);
    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    /// <summary>Call for every input report received.</summary>
    public void OnReport(long? now = null)
    {
        long t = now ?? Stopwatch.GetTimestamp();
        lock (_lock)
        {
            _totalReports++;
            if (_lastReport != 0)
            {
                double gap = Ms(t - _lastReport);
                if (gap >= DropoutMs && t > _suppressUntil) _gaps.Add((t, gap));
            }
            _lastReport = t;

            _recent.Enqueue(t);
            long cutoff = t - Ticks(RateWindow);
            while (_recent.Count > 0 && _recent.Peek() < cutoff) _recent.Dequeue();
            if (t - _created > Ticks(SettleTime))
                _peakRate = Math.Max(_peakRate, _recent.Count / RateWindow.TotalSeconds);

            Trim(t);
        }
    }

    /// <summary>Ignore gaps for a while (e.g. extension initialisation pauses reporting on purpose).</summary>
    public void SuppressDropouts(TimeSpan duration, long? now = null)
    {
        long t = now ?? Stopwatch.GetTimestamp();
        lock (_lock) _suppressUntil = Math.Max(_suppressUntil, t + Ticks(duration));
    }

    public void OnPing(double milliseconds, long? now = null)
    {
        long t = now ?? Stopwatch.GetTimestamp();
        lock (_lock) _pings.Add((t, milliseconds));
    }

    public void OnPingLost(long? now = null)
    {
        long t = now ?? Stopwatch.GetTimestamp();
        lock (_lock) _pings.Add((t, -1));
    }

    public LinkMark Mark(long? now = null)
    {
        long t = now ?? Stopwatch.GetTimestamp();
        lock (_lock) return new LinkMark(t, _totalReports);
    }

    /// <summary>Live view: current report rate, dropouts and pings of the last <paramref name="window"/>.</summary>
    public LinkSnapshot Snapshot(TimeSpan window, long? now = null)
    {
        long t = now ?? Stopwatch.GetTimestamp();
        lock (_lock)
        {
            long rateFrom = t - Ticks(RateWindow);
            int recent = _recent.Count(x => x >= rateFrom);
            return Build(t - Ticks(window), t, recent / RateWindow.TotalSeconds);
        }
    }

    /// <summary>Everything since <paramref name="mark"/> (used by the range test).</summary>
    public LinkSnapshot Measure(LinkMark mark, long? now = null)
    {
        long t = now ?? Stopwatch.GetTimestamp();
        lock (_lock)
        {
            double seconds = Ms(t - mark.Timestamp) / 1000;
            double rate = seconds > 0 ? (_totalReports - mark.TotalReports) / seconds : 0;
            return Build(mark.Timestamp, t, rate);
        }
    }

    private LinkSnapshot Build(long from, long now, double rate)
    {
        int dropouts = 0;
        double longest = 0;
        foreach (var (at, ms) in _gaps)
        {
            if (at < from) continue;
            dropouts++;
            longest = Math.Max(longest, ms);
        }

        // A gap that is still going on right now counts too.
        if (_lastReport != 0 && now > _suppressUntil)
        {
            double open = Ms(now - _lastReport);
            if (open >= DropoutMs)
            {
                dropouts++;
                longest = Math.Max(longest, open);
            }
        }

        double sum = 0;
        int answered = 0, lost = 0;
        foreach (var (at, ms) in _pings)
        {
            if (at < from) continue;
            if (ms < 0) lost++;
            else { sum += ms; answered++; }
        }

        return new LinkSnapshot(
            rate,
            _peakRate,
            dropouts,
            longest,
            answered > 0 ? sum / answered : null,
            lost,
            TimeSpan.FromMilliseconds(Ms(now - _created)));
    }

    private void Trim(long now)
    {
        long cutoff = now - Ticks(History);
        if (_gaps.Count > 0 && _gaps[0].At < cutoff) _gaps.RemoveAll(g => g.At < cutoff);
        if (_pings.Count > 0 && _pings[0].At < cutoff) _pings.RemoveAll(p => p.At < cutoff);
    }
}

/// <summary>Turns measurements into Good / Fair / Poor.</summary>
public static class LinkQuality
{
    /// <summary>Live rating (last minute). Returns the level and 0–4 signal bars.</summary>
    public static (LinkLevel Level, int Bars) Evaluate(LinkSnapshot s)
    {
        if (s.ConnectedFor < TimeSpan.FromSeconds(4)) return (LinkLevel.Unknown, 0);

        double rateRatio = s.PeakReportsPerSecond > 0 ? s.ReportsPerSecond / s.PeakReportsPerSecond : 1;
        double ping = s.AveragePingMs ?? 0;

        if (rateRatio < 0.5 || s.Dropouts >= 6 || ping > 150 || s.LostPings >= 2) return (LinkLevel.Poor, 1);
        if (rateRatio < 0.85 || s.Dropouts >= 2 || ping > 60 || s.LostPings >= 1) return (LinkLevel.Fair, 2);
        return s.Dropouts == 0 && ping <= 30 ? (LinkLevel.Good, 4) : (LinkLevel.Good, 3);
    }
}

public sealed record RangeTestResult(LinkLevel Level, string Title, string Details, string Advice);

/// <summary>A ~20 second test while the player holds the guitar where they normally play.</summary>
public static class RangeTest
{
    public static readonly TimeSpan Duration = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(1);

    private const string Tips =
        "Move closer, or give the Bluetooth adapter a clear line of sight: a USB adapter at the back of the PC " +
        "or next to USB 3 ports often works poorly, and a short USB extension cable to put it on the desk usually " +
        "helps. 2.4 GHz Wi-Fi, other Bluetooth devices and metal (a desk, the PC case) can also interfere.";

    public static RangeTestResult Evaluate(LinkSnapshot s, bool connectionLost)
    {
        if (connectionLost)
            return new RangeTestResult(LinkLevel.Poor, "Connection lost",
                "The Wiimote disconnected during the test. It is out of range here.", Tips);

        double rateRatio = s.PeakReportsPerSecond > 0 ? s.ReportsPerSecond / s.PeakReportsPerSecond : 1;
        string ping = s.AveragePingMs is { } p ? $"{p:0} ms response" : "no response time measured";
        string dropouts = s.Dropouts switch
        {
            0 => "no dropouts",
            1 => $"1 dropout ({s.LongestGapMs:0} ms)",
            _ => $"{s.Dropouts} dropouts, longest {s.LongestGapMs:0} ms",
        };
        string details = $"{dropouts} · {s.ReportsPerSecond:0} updates/s · {ping}" +
                         (s.LostPings > 0 ? $" · {s.LostPings} unanswered" : "");

        if (s.Dropouts >= 5 || s.LongestGapMs >= 250 || s.LostPings >= 2 || rateRatio < 0.6)
            return new RangeTestResult(LinkLevel.Poor, "Poor: expect missed notes", details, Tips);

        if (s.Dropouts >= 1 || (s.AveragePingMs ?? 0) > 60 || s.LostPings >= 1 || rateRatio < 0.85)
            return new RangeTestResult(LinkLevel.Fair, "Fair: occasional dropouts", details, Tips);

        return new RangeTestResult(LinkLevel.Good, "Good: no problems found", details, "");
    }
}
