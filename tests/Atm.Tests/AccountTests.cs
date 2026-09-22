using System.Reflection;
using Atm.Domain;

namespace Atm.Tests;

public class AccountTests
{
    private static readonly DateTimeOffset Now = TestClock.Default;

    [Fact]
    public void Deposit_increases_balance_and_records_an_entry()
    {
        var account = Accounts.Checking(100m);

        var entry = account.Deposit(Money.FromUsd(25.50m), Now);

        Assert.Equal(125.50m, account.Balance.Amount);
        Assert.Equal(TransactionType.Deposit, entry.Type);
        Assert.Equal(125.50m, entry.BalanceAfter.Amount);
        Assert.Equal(entry, Assert.Single(account.PendingEntries));
    }

    [Fact]
    public void Withdraw_decreases_balance()
    {
        var account = Accounts.Checking(100m);

        var entry = account.Withdraw(Money.FromUsd(40m), Now);

        Assert.Equal(60m, account.Balance.Amount);
        Assert.Equal(-40m, entry.SignedAmount);
    }

    [Fact]
    public void Withdrawing_the_entire_balance_is_allowed()
    {
        var account = Accounts.Checking(100m);
        account.Withdraw(Money.FromUsd(100m), Now);
        Assert.True(account.Balance.IsZero);
    }

    [Fact]
    public void Overdraft_is_rejected_and_leaves_the_account_untouched()
    {
        var account = Accounts.Checking(100m);

        var ex = Assert.Throws<InsufficientFundsException>(() => account.Withdraw(Money.FromUsd(100.01m), Now));

        Assert.Equal(100m, ex.Available.Amount);
        Assert.Equal(100m, account.Balance.Amount);
        Assert.Empty(account.PendingEntries);
    }

    [Fact]
    public void Zero_amounts_are_rejected()
    {
        var account = Accounts.Checking();
        Assert.Throws<InvalidAmountException>(() => account.Deposit(Money.Zero, Now));
        Assert.Throws<InvalidAmountException>(() => account.Withdraw(Money.Zero, Now));
    }

    [Fact]
    public void Amounts_over_the_ATM_limit_are_rejected()
    {
        var account = Accounts.Checking();
        var tooMuch = Account.TransactionLimit + Money.FromUsd(0.01m);
        Assert.Throws<InvalidAmountException>(() => account.Deposit(tooMuch, Now));
    }

    [Fact]
    public void Transfer_moves_money_and_links_both_sides()
    {
        var checking = Accounts.Checking(100m);
        var savings = Accounts.Savings(50m);

        var (debit, credit) = checking.TransferTo(savings, Money.FromUsd(30m), Now);

        Assert.Equal(70m, checking.Balance.Amount);
        Assert.Equal(80m, savings.Balance.Amount);
        Assert.Equal(TransactionType.TransferOut, debit.Type);
        Assert.Equal(TransactionType.TransferIn, credit.Type);
        Assert.NotNull(debit.TransferId);
        Assert.Equal(debit.TransferId, credit.TransferId);
        Assert.Equal(savings.Id, debit.CounterpartyAccountId);
        Assert.Equal(checking.Id, credit.CounterpartyAccountId);
    }

    [Fact]
    public void Failed_transfer_changes_neither_account()
    {
        var checking = Accounts.Checking(10m);
        var savings = Accounts.Savings(50m);

        Assert.Throws<InsufficientFundsException>(() => checking.TransferTo(savings, Money.FromUsd(11m), Now));

        Assert.Equal(10m, checking.Balance.Amount);
        Assert.Equal(50m, savings.Balance.Amount);
        Assert.Empty(checking.PendingEntries);
        Assert.Empty(savings.PendingEntries);
    }

    [Fact]
    public void Cannot_transfer_to_the_same_account()
    {
        var checking = Accounts.Checking();
        Assert.Throws<InvalidTransferException>(() => checking.TransferTo(checking, Money.FromUsd(1m), Now));
    }

    [Fact]
    public void Opening_balance_is_recorded_in_the_ledger()
    {
        var account = Account.Open(AccountId.New(), "Checking", AccountType.Checking, "1234", Money.FromUsd(500m), Now);

        var entry = Assert.Single(account.PendingEntries);
        Assert.Equal(TransactionType.OpeningBalance, entry.Type);
        Assert.Equal(500m, account.Balance.Amount);
    }

    [Fact]
    public void Rehydrate_is_not_public()
    {
        // Rehydrate creates a balance with no ledger entry. If it became public, any code
        // could conjure money, so this guards against someone widening it by accident.
        var method = typeof(Account).GetMethod(nameof(Account.Rehydrate), BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        Assert.True(method!.IsAssembly, "Account.Rehydrate should stay internal.");
    }
}
