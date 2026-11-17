using System.Globalization;
using TelloGameRoom.Core.Billing;
using TelloGameRoom.Core.Enums;

namespace TelloGameRoom.Tests.Unit;

public class PriceCalculatorTests
{
    private const long Rate5000 = 500_000; // COP 5,000 per hour, in cents
    private const long Step100 = 10_000;   // COP 100

    [Theory]
    [InlineData(3600, 500_000)]
    [InlineData(1800, 250_000)]
    [InlineData(7200, 1_000_000)]
    [InlineData(10800, 1_500_000)]
    [InlineData(0, 0)]
    public void Presets_at_a_round_rate_need_no_rounding(long seconds, long expected) =>
        Assert.Equal(expected, PriceCalculator.AmountDue(Rate5000, seconds, RoundingMode.Nearest, Step100));

    [Theory]
    // 50 minutes at COP 5,000/h = COP 4,166.67
    [InlineData(RoundingMode.None, 416_667)]
    [InlineData(RoundingMode.Nearest, 420_000)]
    [InlineData(RoundingMode.Up, 420_000)]
    [InlineData(RoundingMode.Down, 410_000)]
    public void Odd_durations_follow_the_rounding_rule(RoundingMode mode, long expected) =>
        Assert.Equal(expected, PriceCalculator.AmountDue(Rate5000, 50 * 60, mode, Step100));

    [Fact]
    public void Nearest_rounds_half_up() =>
        Assert.Equal(20_000, PriceCalculator.Round(15_000, RoundingMode.Nearest, Step100));

    [Fact]
    public void Up_never_undercharges_and_down_never_overcharges()
    {
        for (var minutes = 1; minutes <= 300; minutes++)
        {
            var exact = PriceCalculator.AmountDue(Rate5000, minutes * 60, RoundingMode.None, 0);
            Assert.True(PriceCalculator.AmountDue(Rate5000, minutes * 60, RoundingMode.Up, Step100) >= exact);
            Assert.True(PriceCalculator.AmountDue(Rate5000, minutes * 60, RoundingMode.Down, Step100) <= exact);
        }
    }

    [Fact]
    public void Negative_inputs_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PriceCalculator.AmountDue(-1, 60, RoundingMode.None, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => PriceCalculator.AmountDue(100, -60, RoundingMode.None, 0));
    }

    [Fact]
    public void Cents_round_trip()
    {
        Assert.Equal(500_000, PriceCalculator.ToCents(5000m));
        Assert.Equal(1999, PriceCalculator.ToCents(19.99m));
        Assert.Equal(19.99m, PriceCalculator.FromCents(1999));
    }

    [Fact]
    public void Money_is_formatted_with_the_configured_symbol_and_decimals()
    {
        Assert.Equal("$ 5,000", MoneyFormatter.Format(500_000, "$", 0, CultureInfo.InvariantCulture));
        Assert.Equal("€ 19.99", MoneyFormatter.Format(1999, "€", 2, CultureInfo.InvariantCulture));
        Assert.Equal("$ 5.000", MoneyFormatter.Format(500_000, "$", 0, CultureInfo.GetCultureInfo("es-CO")));
    }
}
