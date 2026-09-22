namespace Atm.Domain;

/// <summary>
/// An immutable, append-only record of a single balance change.
/// <see cref="BalanceAfter"/> makes every entry independently auditable, and the
/// running sum of entries must always equal the account balance.
/// </summary>
public sealed record LedgerEntry(
    Guid Id,
    AccountId AccountId,
    TransactionType Type,
    Money Amount,
    Money BalanceAfter,
    DateTimeOffset OccurredAt,
    Guid? TransferId = null,
    AccountId? CounterpartyAccountId = null)
{
    /// <summary>Amount with sign applied: positive for credits, negative for debits.</summary>
    public decimal SignedAmount => Type.IsCredit() ? Amount.Amount : -Amount.Amount;
}
