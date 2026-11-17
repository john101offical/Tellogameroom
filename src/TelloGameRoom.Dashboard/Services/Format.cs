using System.Globalization;
using TelloGameRoom.Core.Billing;
using TelloGameRoom.Core.Client;
using TelloGameRoom.Core.Dtos;
using TelloGameRoom.Core.Protocol;

namespace TelloGameRoom.Dashboard.Services;

/// <summary>Formatting and parsing that depends on the branch's currency and the UI language.</summary>
public static class Format
{
    public static string Money(long cents, BranchPricingDto? pricing) =>
        MoneyFormatter.Format(cents, pricing?.CurrencySymbol ?? "$", pricing?.CurrencyDecimals ?? 0, Loc.Instance.Culture);

    /// <summary>Plain number for an input box, e.g. "5000" or "19.99".</summary>
    public static string MoneyInput(long cents, int decimals) =>
        PriceCalculator.FromCents(cents).ToString("F" + Math.Clamp(decimals, 0, 2), Loc.Instance.Culture);

    /// <summary>Parses what a person typed as money ("5.000", "5,000", "5000.50"). Returns null when it is not a number.</summary>
    public static long? ParseMoney(string? text)
    {
        var cleaned = text?.Trim().Replace(" ", string.Empty, StringComparison.Ordinal).Replace("$", string.Empty, StringComparison.Ordinal);
        if (string.IsNullOrEmpty(cleaned))
        {
            return null;
        }

        // Try the UI culture first, then invariant, so both "5.000" (es) and "5000.50" (en) work.
        if (decimal.TryParse(cleaned, NumberStyles.Number, Loc.Instance.Culture, out var value)
            || decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out value))
        {
            return value is < 0 or > 1_000_000_000 ? null : PriceCalculator.ToCents(value);
        }

        return null;
    }

    public static string LocalTime(DateTime utc) => utc.ToLocalTime().ToString("g", Loc.Instance.Culture);

    public static string Hours(long seconds) => (seconds / 3600d).ToString("0.#", Loc.Instance.Culture) + " h";

    /// <summary>A message for the user: a translated text for known error codes, the server's detail otherwise.</summary>
    public static string Error(Exception exception)
    {
        if (exception is ApiException api)
        {
            var key = "Err_" + api.Code;
            if (Loc.Instance.Has(key))
            {
                return Loc.T(key);
            }

            if (api.Code == ErrorCodes.Validation && api.Errors is { Count: > 0 })
            {
                return string.Join(" ", api.Errors.Values.SelectMany(v => v).Distinct());
            }

            return api.Message;
        }

        return exception.Message;
    }
}
