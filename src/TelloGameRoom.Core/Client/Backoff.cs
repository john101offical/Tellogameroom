namespace TelloGameRoom.Core.Client;

/// <summary>Exponential backoff with jitter, shared by every reconnect loop.</summary>
public static class Backoff
{
    public static readonly TimeSpan Max = TimeSpan.FromSeconds(30);

    /// <summary>1 s, 2 s, 4 s ... capped at 30 s, with up to 20% random jitter so 100 machines do not reconnect in the same instant.</summary>
    public static TimeSpan Delay(long attempt, Random? random = null)
    {
        var seconds = Math.Min(Max.TotalSeconds, Math.Pow(2, Math.Clamp(attempt, 0, 10)));
        var jitter = 1 + (((random ?? Random.Shared).NextDouble() * 0.4) - 0.2);
        return TimeSpan.FromSeconds(Math.Min(Max.TotalSeconds, seconds * jitter));
    }
}

/// <summary>Formats durations for timers and cards.</summary>
public static class TimeText
{
    /// <summary>"1:05:09" or "05:09".</summary>
    public static string Clock(long seconds)
    {
        seconds = Math.Max(0, seconds);
        var h = seconds / 3600;
        var m = seconds % 3600 / 60;
        var s = seconds % 60;
        return h > 0
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{h}:{m:00}:{s:00}")
            : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{m:00}:{s:00}");
    }

    /// <summary>"1 h 30 min", "45 min".</summary>
    public static string Short(long seconds)
    {
        var minutes = (long)Math.Round(Math.Max(0, seconds) / 60d);
        var h = minutes / 60;
        var m = minutes % 60;
        return h > 0 && m > 0 ? $"{h} h {m} min" : h > 0 ? $"{h} h" : $"{m} min";
    }
}
