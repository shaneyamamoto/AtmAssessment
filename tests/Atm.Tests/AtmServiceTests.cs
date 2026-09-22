using Atm.Application;
using Atm.Domain;
using Atm.Infrastructure;

namespace Atm.Tests;

public class AtmServiceTests
{
    private readonly TestClock _clock = new();
    private readonly InMemoryAccountStore _store;
    private readonly AtmService _atm;
    private readonly Guid _checking = DemoData.CheckingId.Value;
    private readonly Guid _savings = DemoData.SavingsId.Value;

    public AtmServiceTests()
    {
        _store = new InMemoryAccountStore(_clock);
        _atm = new AtmService(_store, _store, _store, _clock);
        DemoData.SeedAsync(_store, _clock).GetAwaiter().GetResult();
    }

    private async Task<decimal> BalanceOf(Guid id) => (await _atm.GetAccountAsync(id)).Balance;

    [Fact]
    public async Task Deposit_is_persisted_and_appears_in_history()
    {
        var result = await _atm.DepositAsync(_checking, 50m, Keys.New());

        Assert.Equal(1_300m, Assert.Single(result.Result.Accounts).Balance);
        Assert.Equal(1_300m, await BalanceOf(_checking));
        var latest = (await _atm.GetHistoryAsync(_checking, 1)).Items.Single();
        Assert.Equal(TransactionType.Deposit, latest.Type);
        Assert.Equal(50m, latest.Amount);
    }

    [Fact]
    public async Task Transfer_updates_both_accounts_and_both_histories()
    {
        var result = await _atm.TransferAsync(_savings, _checking, 200m, Keys.New());

        Assert.Equal(2, result.Result.Accounts.Count);
        Assert.Equal(1_450m, await BalanceOf(_checking));
        Assert.Equal(4_800m, await BalanceOf(_savings));

        var incoming = (await _atm.GetHistoryAsync(_checking, 1)).Items.Single();
        var outgoing = (await _atm.GetHistoryAsync(_savings, 1)).Items.Single();
        Assert.Equal(TransactionType.TransferIn, incoming.Type);
        Assert.Equal("Savings", incoming.CounterpartyName);
        Assert.Equal(outgoing.TransferId, incoming.TransferId);
    }

    [Fact]
    public async Task Failed_withdrawal_leaves_no_trace()
    {
        var before = await _atm.GetHistoryAsync(_checking, 50);

        await Assert.ThrowsAsync<InsufficientFundsException>(() => _atm.WithdrawAsync(_checking, 1_250.01m, Keys.New()));

        Assert.Equal(1_250m, await BalanceOf(_checking));
        Assert.Equal(before.Items.Count, (await _atm.GetHistoryAsync(_checking, 50)).Items.Count);
    }

    [Fact]
    public async Task Unknown_account_is_reported()
    {
        await Assert.ThrowsAsync<AccountNotFoundException>(() => _atm.DepositAsync(Guid.NewGuid(), 10m, Keys.New()));
        await Assert.ThrowsAsync<AccountNotFoundException>(() => _atm.GetHistoryAsync(Guid.NewGuid(), 10));
    }

    [Fact]
    public async Task Invalid_amount_is_rejected_before_any_work()
    {
        await Assert.ThrowsAsync<InvalidAmountException>(() => _atm.WithdrawAsync(_checking, -5m, Keys.New()));
        await Assert.ThrowsAsync<InvalidTransferException>(() => _atm.TransferAsync(_checking, _checking, 5m, Keys.New()));
    }

    [Fact]
    public async Task Concurrent_withdrawals_never_overdraw_and_ledger_reconciles()
    {
        // 200 parallel $10 withdrawals against $1,250: at most 125 can succeed.
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 200).Select(_ => Task.Run(async () =>
        {
            try { await _atm.WithdrawAsync(_checking, 10m, Keys.New()); return true; }
            catch (Exception ex) when (ex is InsufficientFundsException or ConcurrencyConflictException) { return false; }
        })));

        var succeeded = outcomes.Count(ok => ok);
        var balance = await BalanceOf(_checking);

        Assert.True(balance >= 0m);
        Assert.Equal(1_250m - 10m * succeeded, balance);

        var ledger = (await _atm.GetHistoryAsync(_checking, 500)).Items;
        Assert.Equal(balance, ledger.Sum(e => e.SignedAmount));
        Assert.Equal(succeeded, ledger.Count(e => e.Type == TransactionType.Withdrawal));
    }
}
