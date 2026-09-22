using System.Globalization;

namespace Atm.Domain;

/// <summary>
/// A non-negative USD amount with at most two decimal places.
/// Uses <see cref="decimal"/> so arithmetic is exact (no floating-point drift).
/// </summary>
public readonly record struct Money : IComparable<Money>
{
    public decimal Amount { get; }

    private Money(decimal amount) => Amount = amount;

    public static Money Zero => default;

    public bool IsZero => Amount == 0m;

    /// <summary>
    /// Creates an amount in US dollars (e.g. <c>12.50m</c> is $12.50). Throws
    /// <see cref="InvalidAmountException"/> if it is negative or has fractions of a cent.
    /// </summary>
    public static Money FromUsd(decimal amount)
    {
        if (amount < 0m)
            throw new InvalidAmountException("Amount can't be negative.");
        if (decimal.Round(amount, 2) != amount)
            throw new InvalidAmountException("Amount can't have more than two decimal places.");
        return new Money(amount);
    }

    public static Money operator +(Money a, Money b) => new(a.Amount + b.Amount);

    public static Money operator -(Money a, Money b)
    {
        if (b > a) throw new InvalidOperationException("Money can't go negative.");
        return new(a.Amount - b.Amount);
    }

    public static bool operator >(Money a, Money b) => a.Amount > b.Amount;
    public static bool operator <(Money a, Money b) => a.Amount < b.Amount;
    public static bool operator >=(Money a, Money b) => a.Amount >= b.Amount;
    public static bool operator <=(Money a, Money b) => a.Amount <= b.Amount;

    public int CompareTo(Money other) => Amount.CompareTo(other.Amount);

    public override string ToString() => Amount.ToString("C", CultureInfo.GetCultureInfo("en-US"));
}
