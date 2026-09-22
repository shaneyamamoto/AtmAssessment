using Atm.Domain;

namespace Atm.Tests;

public class MoneyTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(0.01)]
    [InlineData(20)]
    [InlineData(1234.5)]
    public void Accepts_non_negative_amounts_with_up_to_two_decimals(decimal amount) =>
        Assert.Equal(amount, Money.FromUsd(amount).Amount);

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.005)]
    [InlineData(0.001)]
    public void Rejects_negative_or_sub_cent_amounts(decimal amount) =>
        Assert.Throws<InvalidAmountException>(() => Money.FromUsd(amount));

    [Fact]
    public void Subtracting_below_zero_is_a_programming_error() =>
        Assert.Throws<InvalidOperationException>(() => Money.FromUsd(1m) - Money.FromUsd(2m));

    [Fact]
    public void Formats_as_US_dollars() =>
        Assert.Equal("$1,234.50", Money.FromUsd(1234.5m).ToString());
}
