using System.Text;
using TelloGameRoom.Core.Security;
using TelloGameRoom.Server.Reports;
using TelloGameRoom.Server.Services;

namespace TelloGameRoom.Tests.Unit;

public class ShiftTotalsTests
{
    [Fact]
    public void Expected_cash_is_opening_plus_sales_minus_refunds()
    {
        var totals = new ShiftTotals(SalesCents: 4_500_000, RefundsCents: 500_000, SessionsStarted: 9);
        Assert.Equal(4_000_000, totals.NetCents);
        Assert.Equal(6_000_000, totals.Expected(openingCashCents: 2_000_000));
    }

    [Theory]
    [InlineData(6_000_000, 6_000_000, 0)]
    [InlineData(5_800_000, 6_000_000, -200_000)] // cash missing
    [InlineData(6_100_000, 6_000_000, 100_000)]  // too much cash
    public void Difference_is_counted_minus_expected(long counted, long expected, long difference) =>
        Assert.Equal(difference, ShiftTotals.Difference(counted, expected));

    [Fact]
    public void A_shift_with_no_sales_expects_only_the_opening_cash() =>
        Assert.Equal(1_000_000, default(ShiftTotals).Expected(1_000_000));
}

public class CsvTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("line1\nline2", "\"line1\nline2\"")]
    public void Cells_are_escaped(string input, string expected) => Assert.Equal(expected, Csv.Escape(input));

    [Theory]
    [InlineData("=SUM(A1)")]
    [InlineData("+1")]
    [InlineData("-1")]
    [InlineData("@cmd")]
    public void Text_that_looks_like_a_formula_is_neutralized(string input) =>
        Assert.StartsWith("'", Csv.Escape(input).Trim('"'), StringComparison.Ordinal);

    [Fact]
    public void Numbers_are_written_invariantly_and_negative_numbers_are_not_mangled()
    {
        Assert.Equal("-2500.5", Csv.Escape(-2500.5m));
        Assert.Equal("2026-03-01", Csv.Escape(new DateOnly(2026, 3, 1)));
        Assert.Equal(string.Empty, Csv.Escape(null));
    }

    [Fact]
    public void Output_starts_with_a_bom_and_uses_crlf()
    {
        var bytes = new Csv().Row("á", 1).Row("b", 2).ToBytes();
        Assert.Equal(Encoding.UTF8.GetPreamble(), bytes[..3]);
        Assert.Equal("á,1\r\nb,2\r\n", Encoding.UTF8.GetString(bytes[3..]));
    }
}

public class PinHasherTests
{
    [Fact]
    public void Pin_verifies_and_wrong_pin_does_not()
    {
        var hash = PinHasher.Hash("482913");
        Assert.StartsWith("pbkdf2$", hash, StringComparison.Ordinal);
        Assert.True(PinHasher.Verify("482913", hash));
        Assert.False(PinHasher.Verify("482914", hash));
        Assert.NotEqual(hash, PinHasher.Hash("482913"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("482913")]
    [InlineData("pbkdf2$1$AAAA$AAAA")]
    [InlineData("pbkdf2$210000$not-base64$zzz")]
    public void Malformed_hashes_never_verify(string? stored) => Assert.False(PinHasher.Verify("482913", stored));
}
