using Atm.Application;
using Atm.Domain;

namespace Atm.Infrastructure;

/// <summary>
/// A thread-safe, in-memory store that behaves like a transactional database:
/// <list type="bullet">
/// <item>Reads return copies, so a half-finished operation never leaks into shared state.</item>
/// <item>A save writes every account in the call (plus the idempotency record) or none of them.</item>
/// <item>Each account row has a version number. Saving an account that changed since it was
/// loaded is rejected, which is what a SQL rowversion or EF Core concurrency token would do.</item>
/// <item>History is paged by ledger position, like
/// <c>WHERE position &lt; @cursor ORDER BY position DESC</c> on a real table.</item>
/// </list>
/// Moving to a real database means implementing the same interfaces; nothing above this layer changes.
/// </summary>
public sealed class InMemoryAccountStore(TimeProvider? clock = null)
    : IAccountRepository, ITransactionHistory, IIdempotencyStore
{
    /// <summary>How long a completed request can be replayed by its idempotency key.</summary>
    public static readonly TimeSpan IdempotencyRetention = TimeSpan.FromHours(24);

    /// <summary>One stored account. Kept separate from <see cref="Account"/> so callers can't change stored state directly.</summary>
    private sealed record AccountRow(
        AccountId Id,
        string Name,
        AccountType Type,
        string NumberSuffix,
        Money Balance,
        long Version);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>Every read and write takes this lock, which is what makes saves atomic.</summary>
    private readonly object _lock = new();

    private readonly Dictionary<AccountId, AccountRow> _accounts = [];

    /// <summary>
    /// Every ledger entry for every account, in the order they were saved. An entry's index
    /// in this list is its "ledger position", which only ever increases.
    /// </summary>
    private readonly List<LedgerEntry> _ledger = [];

    /// <summary>
    /// For each account, the ledger positions of its entries, in ascending order. This plays
    /// the role of a database index on (account_id, position), so paging doesn't scan everything.
    /// </summary>
    private readonly Dictionary<AccountId, List<int>> _ledgerPositionsByAccount = [];

    private readonly Dictionary<string, IdempotencyRecord> _idempotencyRecords = new(StringComparer.Ordinal);

    // ---------------------------------------------------------------------------------
    // Reading accounts
    // ---------------------------------------------------------------------------------

    public Task<Account?> FindAsync(AccountId id, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (!_accounts.TryGetValue(id, out var row))
            {
                return Task.FromResult<Account?>(null);
            }

            return Task.FromResult<Account?>(ToAccount(row));
        }
    }

    public Task<IReadOnlyList<Account>> ListAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            IReadOnlyList<Account> allAccounts = _accounts.Values
                .OrderBy(row => row.Type)
                .ThenBy(row => row.Name, StringComparer.Ordinal)
                .Select(ToAccount)
                .ToList();

            return Task.FromResult(allAccounts);
        }
    }

    // ---------------------------------------------------------------------------------
    // Saving
    // ---------------------------------------------------------------------------------

    public Task SaveAsync(
        IReadOnlyCollection<Account> accounts,
        IdempotencyRecord? idempotencyRecord = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        lock (_lock)
        {
            // Check everything first, then write. Nothing in the write step can fail,
            // so either all of the changes are applied or none of them are.
            ThrowIfAnythingConflicts(accounts, idempotencyRecord);
            WriteChanges(accounts, idempotencyRecord);
        }

        return Task.CompletedTask;
    }

    /// <summary>Caller must hold <see cref="_lock"/>.</summary>
    private void ThrowIfAnythingConflicts(IReadOnlyCollection<Account> accounts, IdempotencyRecord? idempotencyRecord)
    {
        foreach (var account in accounts)
        {
            // A brand-new account isn't stored yet, and was created with version 0.
            var storedVersion = _accounts.TryGetValue(account.Id, out var row) ? row.Version : 0;

            var changedSinceLoaded = storedVersion != account.Version;
            if (changedSinceLoaded)
            {
                throw new ConcurrencyConflictException(
                    $"Account {account.Id} was changed by another operation. Please try again.");
            }
        }

        if (idempotencyRecord is not null)
        {
            var keyAlreadyUsed = FindUnexpiredRecord(idempotencyRecord.Key) is not null;
            if (keyAlreadyUsed)
            {
                // Another request with the same key finished first. The caller retries,
                // finds that request's record, and replays its response.
                throw new ConcurrencyConflictException("A request with this idempotency key has already completed.");
            }
        }
    }

    /// <summary>Caller must hold <see cref="_lock"/>, and must have called <see cref="ThrowIfAnythingConflicts"/> first.</summary>
    private void WriteChanges(IReadOnlyCollection<Account> accounts, IdempotencyRecord? idempotencyRecord)
    {
        foreach (var account in accounts)
        {
            _accounts[account.Id] = new AccountRow(
                account.Id,
                account.Name,
                account.Type,
                account.NumberSuffix,
                account.Balance,
                Version: account.Version + 1);

            foreach (var entry in account.PendingEntries)
            {
                AppendToLedger(entry);
            }
        }

        if (idempotencyRecord is not null)
        {
            RemoveExpiredIdempotencyRecords();
            _idempotencyRecords[idempotencyRecord.Key] = idempotencyRecord;
        }
    }

    /// <summary>Caller must hold <see cref="_lock"/>.</summary>
    private void AppendToLedger(LedgerEntry entry)
    {
        var position = _ledger.Count;
        _ledger.Add(entry);

        if (!_ledgerPositionsByAccount.TryGetValue(entry.AccountId, out var accountPositions))
        {
            accountPositions = [];
            _ledgerPositionsByAccount[entry.AccountId] = accountPositions;
        }

        accountPositions.Add(position);
    }

    // ---------------------------------------------------------------------------------
    // Transaction history
    // ---------------------------------------------------------------------------------

    public Task<LedgerPage> GetPageAsync(AccountId accountId, int limit, string? cursor, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        // No cursor means "start from the newest entry", i.e. before a position larger than any real one.
        var readBeforePosition = cursor is null ? int.MaxValue : LedgerCursor.Decode(cursor);

        lock (_lock)
        {
            if (!_ledgerPositionsByAccount.TryGetValue(accountId, out var accountPositions))
            {
                return Task.FromResult(new LedgerPage(Entries: [], NextCursor: null));
            }

            // accountPositions[0 .. olderCount-1] are the entries older than the cursor.
            // The page is the newest `limit` of those, so it ends just before olderCount.
            var olderCount = CountPositionsBefore(accountPositions, readBeforePosition);
            var pageStartIndex = Math.Max(0, olderCount - limit);

            // Walk backwards so the page comes out newest first.
            var entries = new List<LedgerEntry>();
            for (var index = olderCount - 1; index >= pageStartIndex; index--)
            {
                var position = accountPositions[index];
                entries.Add(_ledger[position]);
            }

            var hasOlderEntries = pageStartIndex > 0;
            var nextCursor = hasOlderEntries ? LedgerCursor.Encode(accountPositions[pageStartIndex]) : null;

            return Task.FromResult(new LedgerPage(entries, nextCursor));
        }
    }

    /// <summary>
    /// How many of the positions in an ascending list are smaller than <paramref name="position"/>.
    /// Uses a binary search, so it stays fast however long the history gets.
    /// </summary>
    private static int CountPositionsBefore(List<int> ascendingPositions, int position)
    {
        var index = ascendingPositions.BinarySearch(position);

        // If the value is in the list, BinarySearch returns its index, which is also the
        // number of smaller values. If it isn't, BinarySearch returns the bitwise complement
        // (~) of the index where it would be inserted; flipping it back gives that index.
        var valueWasFound = index >= 0;
        return valueWasFound ? index : ~index;
    }

    // ---------------------------------------------------------------------------------
    // Idempotency records
    // ---------------------------------------------------------------------------------

    public Task<IdempotencyRecord?> FindAsync(string key, CancellationToken ct = default)
    {
        lock (_lock)
        {
            return Task.FromResult(FindUnexpiredRecord(key));
        }
    }

    /// <summary>Caller must hold <see cref="_lock"/>. Expired records are treated as if they don't exist.</summary>
    private IdempotencyRecord? FindUnexpiredRecord(string key)
    {
        if (!_idempotencyRecords.TryGetValue(key, out var record))
        {
            return null;
        }

        return IsExpired(record) ? null : record;
    }

    /// <summary>
    /// Caller must hold <see cref="_lock"/>. This scans every record, but only runs when a
    /// request with a key is saved, which is fine for an in-memory demo store.
    /// </summary>
    private void RemoveExpiredIdempotencyRecords()
    {
        var expiredKeys = _idempotencyRecords
            .Where(pair => IsExpired(pair.Value))
            .Select(pair => pair.Key)
            .ToList();

        foreach (var key in expiredKeys)
        {
            _idempotencyRecords.Remove(key);
        }
    }

    private bool IsExpired(IdempotencyRecord record)
    {
        var age = _clock.GetUtcNow() - record.CreatedAt;
        return age >= IdempotencyRetention;
    }

    private static Account ToAccount(AccountRow row) =>
        Account.Rehydrate(row.Id, row.Name, row.Type, row.NumberSuffix, row.Balance, row.Version);
}
