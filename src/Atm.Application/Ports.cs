using Atm.Domain;

namespace Atm.Application;

/// <summary>Write-side persistence for account aggregates.</summary>
public interface IAccountRepository
{
    Task<Account?> FindAsync(AccountId id, CancellationToken ct = default);

    Task<IReadOnlyList<Account>> ListAsync(CancellationToken ct = default);

    /// <summary>
    /// Atomically persists the new balances and pending ledger entries of all given
    /// accounts, plus the idempotency record if one is supplied: either everything is
    /// written or nothing is. Throws <see cref="ConcurrencyConflictException"/> if any
    /// account changed since it was loaded, or if the idempotency key was already used.
    /// </summary>
    Task SaveAsync(
        IReadOnlyCollection<Account> accounts,
        IdempotencyRecord? idempotencyRecord = null,
        CancellationToken ct = default);
}

/// <summary>One page of ledger entries, newest first.</summary>
/// <param name="NextCursor">Opaque cursor for the next (older) page, or null when there are no more.</param>
public sealed record LedgerPage(IReadOnlyList<LedgerEntry> Entries, string? NextCursor);

/// <summary>Read-side query for transaction history (kept separate from the aggregate).</summary>
public interface ITransactionHistory
{
    /// <summary>
    /// Keyset-paged history, newest first. <paramref name="cursor"/> is an opaque value from a
    /// previous page. Pages are stable: entries recorded after the first page was read never
    /// shift later pages. Throws <see cref="InvalidCursorException"/> for a malformed cursor.
    /// </summary>
    Task<LedgerPage> GetPageAsync(AccountId accountId, int limit, string? cursor, CancellationToken ct = default);
}

/// <summary>A completed request, remembered so a retry with the same key replays the same response.</summary>
/// <param name="Fingerprint">What was asked for; a retry must match it exactly.</param>
public sealed record IdempotencyRecord(string Key, string Fingerprint, OperationResult Result, DateTimeOffset CreatedAt);

public interface IIdempotencyStore
{
    /// <summary>The record for <paramref name="key"/>, or null if unknown or expired.</summary>
    Task<IdempotencyRecord?> FindAsync(string key, CancellationToken ct = default);
}
