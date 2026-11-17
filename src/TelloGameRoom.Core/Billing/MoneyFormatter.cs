using System.Globalization;

namespace TelloGameRoom.Core.Billing;

/// <summary>Formats cent amounts for display using the branch's currency settings.</summary>
public static class MoneyFormatter
{
    /// <param name="cents">Amount in cents.</param>
    /// <param name="symbol">Currency symbol, e.g. "$".</param>
    /// <param name="decimals">Number of decimals to show (0 for COP).</param>
    /// <param name="culture">Culture used for group and decimal separators. Defaults to the current culture.</param>
    public static string Format(long cents, string symbol, int decimals, CultureInfo? culture = null)
    {
        decimals = Math.Clamp(decimals, 0, 2);
        var number = PriceCalculator.FromCents(cents).ToString("N" + decimals, culture ?? CultureInfo.CurrentCulture);
        return string.IsNullOrEmpty(symbol) ? number : $"{symbol} {number}";
    }
}
