using System.Globalization;
using System.Text;

namespace TelloGameRoom.Server.Reports;

/// <summary>Minimal RFC 4180 CSV writer that also neutralizes spreadsheet formula injection.</summary>
public sealed class Csv
{
    private readonly StringBuilder _sb = new();

    public Csv Row(params object?[] cells)
    {
        for (var i = 0; i < cells.Length; i++)
        {
            if (i > 0)
            {
                _sb.Append(',');
            }

            _sb.Append(Escape(cells[i]));
        }

        _sb.Append("\r\n");
        return this;
    }

    /// <summary>UTF-8 with a byte-order mark so Excel opens accented characters correctly.</summary>
    public byte[] ToBytes() => [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(_sb.ToString())];

    public static string Escape(object? cell)
    {
        var text = cell switch
        {
            null => string.Empty,
            DateTime d => d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => cell.ToString() ?? string.Empty,
        };

        // A text cell that starts with = + - @ would be run as a formula by Excel. Numbers are left alone.
        if (cell is string && text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            text = "'" + text;
        }

        return text.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? $"\"{text.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : text;
    }
}
