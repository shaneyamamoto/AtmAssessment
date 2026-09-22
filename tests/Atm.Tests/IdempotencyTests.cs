using Atm.Application;
using Atm.Domain;
using Atm.Infrastructure;

namespace Atm.Tests;

public class IdempotencyTests
{
    private readonly TestClock _clock = new();
    private readonly InMemoryAccountStore _store;
    private readonly AtmService _atm;
    private readonly Guid _checking = DemoData.CheckingId.Value;
    private readonly Guid _savings = DemoData.SavingsId.Value;

    public IdempotencyTests()
    {
        _store = new InMemoryAccountStore(_clock);
        _atm = new AtmService(_store, _store, _store, _clock);
        DemoData.SeedAsync(_store, _clock).GetAwaiter().GetResult();
    }

    private async Task<decimal> BalanceOf(Guid id) => (await _atm.GetAccountAsync(id)).Balance;

    private async Task<int> WithdrawalCount() =>
        (await _atm.GetHistoryAsync(_checking, 100)).Items.Count(t => t.Type == TransactionType.Withdrawal);

    [Fact]
    public async Task Retrying_with_the_same_key_replays_without_moving_money_again()
    {
        var first = await _atm.WithdrawAsync(_checking, 60m, "key-1");
        var retry = await _atm.WithdrawAsync(_checking, 60m, "key-1");

        Assert.False(first.Replayed);
        Assert.True(retry.Replayed);
        Assert.Equal(first.Result, retry.Result);
        Assert.Equal(1_190m, await BalanceOf(_checking));
        Assert.Equal(1, await WithdrawalCount());
    }

    [Fact]
    public async Task Replay_returns_the_original_response_even_after_later_changes()
    {
        var first = await _atm.DepositAsync(_checking, 10m, "key-1");
        await _atm.DepositAsync(_checking, 500m, "key-2");

        var replay = await _atm.DepositAsync(_checking, 10m, "key-1");

        Assert.Equal(1_260m, replay.Result.Accounts.Single().Balance);
        Assert.Equal(first.Result.Transactions.Single().Id, replay.Result.Transactions.Single().Id);
    }

    [Theory]
    [InlineData("withdraw", 61)]   // different amount
    [InlineData("deposit", 60)]    // different operation
    public async Task Reusing_a_key_for_a_different_request_is_rejected(string operation, decimal amount)
    {
        await _atm.WithdrawAsync(_checking, 60m, "key-1");

        Task Act() => operation == "withdraw"
            ? _atm.WithdrawAsync(_checking, amount, "key-1")
            : _atm.DepositAsync(_checking, amount, "key-1");

        await Assert.ThrowsAsync<IdempotencyKeyReusedException>(Act);
        Assert.Equal(1_190m, await BalanceOf(_checking));
    }

    [Fact]
    public async Task Transfer_retry_moves_money_once()
    {
        await _atm.TransferAsync(_savings, _checking, 100m, "key-1");
        var retry = await _atm.TransferAsync(_savings, _checking, 100m, "key-1");

        Assert.True(retry.Replayed);
        Assert.Equal(1_350m, await BalanceOf(_checking));
        Assert.Equal(4_900m, await BalanceOf(_savings));
    }

    [Fact]
    public async Task Concurrent_requests_with_one_key_move_money_exactly_once()
    {
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => Task.Run(() => _atm.WithdrawAsync(_checking, 25m, "same-key"))));

        Assert.Equal(1, outcomes.Count(o => !o.Replayed));
        Assert.Single(outcomes.Select(o => o.Result.Transactions.Single().Id).Distinct());
        Assert.Equal(1_225m, await BalanceOf(_checking));
        Assert.Equal(1, await WithdrawalCount());
    }

    [Fact]
    public async Task Rejected_requests_are_not_remembered()
    {
        await Assert.ThrowsAsync<InsufficientFundsException>(() => _atm.WithdrawAsync(_checking, 2_000m, "key-1"));

        // Nothing happened, so the same key re-evaluates against the current balance.
        await _atm.DepositAsync(_checking, 1_000m, Keys.New());
        var retry = await _atm.WithdrawAsync(_checking, 2_000m, "key-1");

        Assert.False(retry.Replayed);
        Assert.Equal(250m, await BalanceOf(_checking));
    }

    [Fact]
    public async Task Different_keys_are_separate_requests()
    {
        // Two identical withdrawals with different keys are two real withdrawals, not a retry.
        await _atm.WithdrawAsync(_checking, 10m, "key-1");
        await _atm.WithdrawAsync(_checking, 10m, "key-2");

        Assert.Equal(2, await WithdrawalCount());
        Assert.Equal(1_230m, await BalanceOf(_checking));
    }

    [Fact]
    public async Task A_key_is_required()
    {
        // The parameter is non-nullable, so this only happens if a caller ignores the compiler warning.
        await Assert.ThrowsAsync<ArgumentNullException>(() => _atm.DepositAsync(_checking, 1m, null!));
        Assert.Equal(1_250m, await BalanceOf(_checking));
    }

    [Fact]
    public async Task Expired_key_is_treated_as_new()
    {
        await _atm.WithdrawAsync(_checking, 10m, "key-1");
        _clock.Advance(InMemoryAccountStore.IdempotencyRetention);

        var later = await _atm.WithdrawAsync(_checking, 10m, "key-1");

        Assert.False(later.Replayed);
        Assert.Equal(2, await WithdrawalCount());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Blank_keys_are_rejected(string key) =>
        await Assert.ThrowsAsync<InvalidIdempotencyKeyException>(() => _atm.DepositAsync(_checking, 1m, key));

    [Fact]
    public async Task Overlong_keys_are_rejected() =>
        await Assert.ThrowsAsync<InvalidIdempotencyKeyException>(() =>
            _atm.DepositAsync(_checking, 1m, new string('k', AtmService.MaxIdempotencyKeyLength + 1)));
}
