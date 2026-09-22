using Atm.Application;
using Atm.Domain;
using Atm.Infrastructure;

namespace Atm.Tests;

public class InMemoryAccountStoreTests
{
    private readonly TestClock _clock = new();
    private readonly InMemoryAccountStore _store;
    private readonly DateTimeOffset _now = TestClock.Default;

    public InMemoryAccountStoreTests() => _store = new InMemoryAccountStore(_clock);

    private async Task<(AccountId A, AccountId B)> SeedTwoAsync()
    {
        var a = Account.Open(AccountId.New(), "A", AccountType.Checking, "1111", Money.FromUsd(100m), _now);
        var b = Account.Open(AccountId.New(), "B", AccountType.Savings, "2222", Money.FromUsd(100m), _now);
        await _store.SaveAsync([a, b]);
        return (a.Id, b.Id);
    }

    [Fact]
    public async Task Reads_are_detached_copies()
    {
        var (a, _) = await SeedTwoAsync();

        var loaded = (await _store.FindAsync(a))!;
        loaded.Deposit(Money.FromUsd(50m), _now); // not saved

        Assert.Equal(100m, (await _store.FindAsync(a))!.Balance.Amount);
    }

    [Fact]
    public async Task Stale_write_is_rejected()
    {
        var (a, _) = await SeedTwoAsync();
        var first = (await _store.FindAsync(a))!;
        var second = (await _store.FindAsync(a))!;

        first.Withdraw(Money.FromUsd(80m), _now);
        await _store.SaveAsync([first]);
        second.Withdraw(Money.FromUsd(80m), _now);

        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => _store.SaveAsync([second]));
        Assert.Equal(20m, (await _store.FindAsync(a))!.Balance.Amount);
    }

    [Fact]
    public async Task Multi_account_save_is_all_or_nothing()
    {
        var (a, b) = await SeedTwoAsync();
        var fromA = (await _store.FindAsync(a))!;
        var toB = (await _store.FindAsync(b))!;

        // Someone else modifies B in between.
        var otherB = (await _store.FindAsync(b))!;
        otherB.Deposit(Money.FromUsd(1m), _now);
        await _store.SaveAsync([otherB]);

        fromA.TransferTo(toB, Money.FromUsd(30m), _now);
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => _store.SaveAsync([fromA, toB]));

        Assert.Equal(100m, (await _store.FindAsync(a))!.Balance.Amount);
        Assert.DoesNotContain((await _store.GetPageAsync(a, 10, null)).Entries, e => e.Type == TransactionType.TransferOut);
    }

    private async Task DepositAsync(AccountId id, decimal amount, IdempotencyRecord? record = null)
    {
        var account = (await _store.FindAsync(id))!;
        account.Deposit(Money.FromUsd(amount), _now);
        await _store.SaveAsync([account], record);
    }

    [Fact]
    public async Task First_page_is_newest_first_with_a_cursor_for_more()
    {
        var (a, _) = await SeedTwoAsync();
        for (var i = 1; i <= 3; i++) await DepositAsync(a, i);

        var page = await _store.GetPageAsync(a, 2, null);

        Assert.Equal([3m, 2m], page.Entries.Select(e => e.Amount.Amount));
        Assert.NotNull(page.NextCursor);
    }

    [Fact]
    public async Task Walking_all_pages_returns_every_entry_exactly_once()
    {
        var (a, b) = await SeedTwoAsync();
        for (var i = 1; i <= 10; i++)
        {
            await DepositAsync(a, i);
            await DepositAsync(b, i); // interleaved entries for another account must be skipped
        }

        var seen = new List<decimal>();
        string? cursor = null;
        do
        {
            var page = await _store.GetPageAsync(a, 3, cursor);
            seen.AddRange(page.Entries.Select(e => e.Amount.Amount));
            cursor = page.NextCursor;
        } while (cursor is not null);

        Assert.Equal([10m, 9m, 8m, 7m, 6m, 5m, 4m, 3m, 2m, 1m, 100m], seen);
    }

    [Fact]
    public async Task Later_pages_do_not_shift_when_new_entries_arrive()
    {
        var (a, _) = await SeedTwoAsync();
        for (var i = 1; i <= 4; i++) await DepositAsync(a, i);

        var first = await _store.GetPageAsync(a, 2, null);   // 4, 3
        await DepositAsync(a, 99);                           // arrives between page loads
        var second = await _store.GetPageAsync(a, 2, first.NextCursor);

        Assert.Equal([2m, 1m], second.Entries.Select(e => e.Amount.Amount));
    }

    [Fact]
    public async Task Last_page_has_no_cursor()
    {
        var (a, _) = await SeedTwoAsync();
        var page = await _store.GetPageAsync(a, 10, null);
        Assert.Single(page.Entries);
        Assert.Null(page.NextCursor);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("djE6LTE")]   // "v1:-1"
    [InlineData("djI6NQ")]    // "v2:5"
    public async Task Malformed_cursor_is_rejected(string cursor)
    {
        var (a, _) = await SeedTwoAsync();
        await Assert.ThrowsAsync<InvalidCursorException>(() => _store.GetPageAsync(a, 10, cursor));
    }

    private static IdempotencyRecord Record(string key) =>
        new(key, "fingerprint", new OperationResult([], []), TestClock.Default);

    [Fact]
    public async Task Idempotency_record_is_saved_with_the_balance_change()
    {
        var (a, _) = await SeedTwoAsync();
        await DepositAsync(a, 5m, Record("k"));

        Assert.NotNull(await _store.FindAsync("k"));
        Assert.Equal(105m, (await _store.FindAsync(a))!.Balance.Amount);
    }

    [Fact]
    public async Task Idempotency_record_is_not_saved_when_the_write_conflicts()
    {
        var (a, _) = await SeedTwoAsync();
        var stale = (await _store.FindAsync(a))!;
        await DepositAsync(a, 1m);

        stale.Deposit(Money.FromUsd(5m), _now);
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => _store.SaveAsync([stale], Record("k")));

        Assert.Null(await _store.FindAsync("k"));
    }

    [Fact]
    public async Task Saving_a_used_key_again_is_a_conflict()
    {
        var (a, _) = await SeedTwoAsync();
        await DepositAsync(a, 5m, Record("k"));

        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => DepositAsync(a, 5m, Record("k")));
        Assert.Equal(105m, (await _store.FindAsync(a))!.Balance.Amount);
    }

    [Fact]
    public async Task Idempotency_keys_expire_after_the_retention_period()
    {
        var (a, _) = await SeedTwoAsync();
        await DepositAsync(a, 5m, Record("k"));

        _clock.Advance(InMemoryAccountStore.IdempotencyRetention - TimeSpan.FromSeconds(1));
        Assert.NotNull(await _store.FindAsync("k"));

        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(await _store.FindAsync("k"));
        await DepositAsync(a, 5m, Record("k")); // key can be reused once expired
    }
}
