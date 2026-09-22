namespace Atm.Domain;

/// <summary>
/// Aggregate root for a bank account. It owns the balance and is the only place
/// that can change it, so the invariants (positive amounts, no overdraft, every
/// change recorded in the ledger) are enforced in one spot.
///
/// The account does not hold its full history: new ledger entries are collected in
/// <see cref="PendingEntries"/> and the repository appends them atomically together
/// with the new balance. History is read through a separate query path.
/// </summary>
public sealed class Account
{
    /// <summary>Largest single deposit, withdrawal or transfer the ATM accepts.</summary>
    public static readonly Money TransactionLimit = Money.FromUsd(10_000m);

    private readonly List<LedgerEntry> _pendingEntries = [];

    public AccountId Id { get; }
    public string Name { get; }
    public AccountType Type { get; }

    /// <summary>Last four digits shown on screen.</summary>
    public string NumberSuffix { get; }

    public Money Balance { get; private set; }

    /// <summary>Version the account was loaded at; used for optimistic concurrency.</summary>
    public long Version { get; }

    public IReadOnlyList<LedgerEntry> PendingEntries => _pendingEntries;

    private Account(AccountId id, string name, AccountType type, string numberSuffix, Money balance, long version)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Name is required.", nameof(name));
        }

        Id = id;
        Name = name;
        Type = type;
        NumberSuffix = numberSuffix;
        Balance = balance;
        Version = version;
    }

    /// <summary>Opens a new account. A non-zero opening balance is recorded in the ledger.</summary>
    public static Account Open(
        AccountId id, string name, AccountType type, string numberSuffix, Money openingBalance, DateTimeOffset openedAt)
    {
        var account = new Account(id, name, type, numberSuffix, balance: Money.Zero, version: 0);

        // Record the opening balance as a ledger entry, so the ledger always adds up to the balance.
        if (!openingBalance.IsZero)
        {
            account.Credit(TransactionType.OpeningBalance, openingBalance, openedAt);
        }

        return account;
    }

    /// <summary>
    /// Recreates an account from persisted state. Internal because it sets a balance
    /// without a ledger entry: only persistence (and tests) may call it, via
    /// InternalsVisibleTo in the project file. Everything else must use <see cref="Open"/>,
    /// which records the opening balance in the ledger.
    /// </summary>
    internal static Account Rehydrate(
        AccountId id, string name, AccountType type, string numberSuffix, Money balance, long version) =>
        new(id, name, type, numberSuffix, balance, version);

    public LedgerEntry Deposit(Money amount, DateTimeOffset occurredAt)
    {
        EnsureValidTransactionAmount(amount);
        return Credit(TransactionType.Deposit, amount, occurredAt);
    }

    public LedgerEntry Withdraw(Money amount, DateTimeOffset occurredAt)
    {
        EnsureValidTransactionAmount(amount);
        return Debit(TransactionType.Withdrawal, amount, occurredAt);
    }

    /// <summary>
    /// Moves money to another account. Both ledger entries share a transfer id so the two
    /// sides can be matched up later. The debit happens first and checks the balance, so if
    /// there isn't enough money, neither account changes.
    /// </summary>
    public TransferEntries TransferTo(Account destination, Money amount, DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(destination);

        if (destination.Id == Id)
        {
            throw new InvalidTransferException("Choose two different accounts to transfer between.");
        }

        EnsureValidTransactionAmount(amount);

        var transferId = Guid.NewGuid();

        var debit = Debit(
            TransactionType.TransferOut, amount, occurredAt,
            transferId: transferId, counterparty: destination.Id);

        var credit = destination.Credit(
            TransactionType.TransferIn, amount, occurredAt,
            transferId: transferId, counterparty: Id);

        return new TransferEntries(debit, credit);
    }

    private LedgerEntry Credit(
        TransactionType type,
        Money amount,
        DateTimeOffset occurredAt,
        Guid? transferId = null,
        AccountId? counterparty = null)
    {
        Balance += amount;
        return RecordEntry(type, amount, occurredAt, transferId, counterparty);
    }

    private LedgerEntry Debit(
        TransactionType type,
        Money amount,
        DateTimeOffset occurredAt,
        Guid? transferId = null,
        AccountId? counterparty = null)
    {
        if (amount > Balance)
        {
            throw new InsufficientFundsException(Id, available: Balance, requested: amount);
        }

        Balance -= amount;
        return RecordEntry(type, amount, occurredAt, transferId, counterparty);
    }

    /// <summary>Adds a ledger entry for a change that has just been applied to the balance.</summary>
    private LedgerEntry RecordEntry(
        TransactionType type,
        Money amount,
        DateTimeOffset occurredAt,
        Guid? transferId,
        AccountId? counterparty)
    {
        var entry = new LedgerEntry(
            Id: Guid.NewGuid(),
            AccountId: Id,
            Type: type,
            Amount: amount,
            BalanceAfter: Balance,
            OccurredAt: occurredAt,
            TransferId: transferId,
            CounterpartyAccountId: counterparty);

        _pendingEntries.Add(entry);
        return entry;
    }

    private static void EnsureValidTransactionAmount(Money amount)
    {
        if (amount.IsZero)
        {
            throw new InvalidAmountException("Amount must be greater than zero.");
        }

        if (amount > TransactionLimit)
        {
            throw new InvalidAmountException($"The ATM limit is {TransactionLimit} per transaction.");
        }
    }
}

/// <summary>The two ledger entries a transfer creates: money out of one account and into the other.</summary>
public sealed record TransferEntries(LedgerEntry Debit, LedgerEntry Credit);
