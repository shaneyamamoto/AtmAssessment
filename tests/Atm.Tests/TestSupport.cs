using Atm.Domain;

namespace Atm.Tests;

/// <summary>A clock that only moves when the test says so.</summary>
internal sealed class TestClock(DateTimeOffset start) : TimeProvider
{
    public static readonly DateTimeOffset Default = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
    private DateTimeOffset _now = start;

    public TestClock() : this(Default) { }

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

internal static class Accounts
{
    public static Account Checking(decimal balance = 100m) => Rehydrated("Checking", AccountType.Checking, balance);
    public static Account Savings(decimal balance = 100m) => Rehydrated("Savings", AccountType.Savings, balance);

    private static Account Rehydrated(string name, AccountType type, decimal balance) =>
        Account.Rehydrate(AccountId.New(), name, type, "0000", Money.FromUsd(balance), version: 1);
}

/// <summary>Every money movement needs an idempotency key; tests that don't care about retries just use a fresh one.</summary>
internal static class Keys
{
    public static string New() => Guid.NewGuid().ToString();
}
