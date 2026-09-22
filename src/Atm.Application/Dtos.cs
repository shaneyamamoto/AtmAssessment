using Atm.Domain;

namespace Atm.Application;

// The shapes the application hands back to callers (and the API sends as JSON).
// They're plain data with no behavior, kept separate from the domain classes so
// internals like Account.Version never leak out, and the domain can change without
// changing what clients receive.

/// <summary>
/// The public view of an <see cref="Account"/>: what a client needs to show it,
/// without internal details like the concurrency version or unsaved ledger entries.
/// </summary>
public sealed record AccountDto(Guid Id, string Name, AccountType Type, string NumberSuffix, decimal Balance);

/// <summary>
/// The public view of a <see cref="LedgerEntry"/>, with the other account's name filled
/// in for transfers so the client can show "Transfer to Savings" without another lookup.
/// </summary>
public sealed record TransactionDto(
    Guid Id,
    Guid AccountId,
    TransactionType Type,
    decimal Amount,
    decimal SignedAmount,
    decimal BalanceAfter,
    DateTimeOffset OccurredAt,
    Guid? TransferId,
    Guid? CounterpartyAccountId,
    string? CounterpartyName);

/// <summary>A page of history, newest first.</summary>
public sealed record TransactionPage(IReadOnlyList<TransactionDto> Items, string? NextCursor);

/// <summary>Result of a money movement: the affected accounts and the entries written.</summary>
public sealed record OperationResult(IReadOnlyList<AccountDto> Accounts, IReadOnlyList<TransactionDto> Transactions);

/// <summary>An operation result, flagged when it was replayed from an earlier request with the same key.</summary>
public sealed record OperationOutcome(OperationResult Result, bool Replayed);
