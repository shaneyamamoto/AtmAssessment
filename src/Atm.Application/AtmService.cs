using System.Globalization;
using Atm.Domain;

namespace Atm.Application;

/// <summary>
/// One method per ATM use case. Each one loads the accounts it needs, asks the domain
/// to make the change, and saves the result as a single atomic write. The business
/// rules themselves live in <see cref="Account"/>; this class only coordinates.
/// </summary>
public sealed class AtmService(
    IAccountRepository accounts,
    ITransactionHistory history,
    IIdempotencyStore idempotency,
    TimeProvider clock)
{
    /// <summary>How many times to try an operation when another request changes the same account first.</summary>
    internal const int MaxAttempts = 3;

    public const int MaxIdempotencyKeyLength = 100;

    /// <summary>What a money movement changed: the accounts to save and the ledger entries it created.</summary>
    private sealed record MoneyMovement(
        IReadOnlyCollection<Account> ChangedAccounts,
        IReadOnlyList<LedgerEntry> NewEntries);

    // ---------------------------------------------------------------------------------
    // Queries
    // ---------------------------------------------------------------------------------

    public async Task<IReadOnlyList<AccountDto>> GetAccountsAsync(CancellationToken ct = default)
    {
        var allAccounts = await accounts.ListAsync(ct);
        return allAccounts.Select(ToAccountDto).ToList();
    }

    public async Task<AccountDto> GetAccountAsync(Guid accountId, CancellationToken ct = default)
    {
        var account = await LoadAccountAsync(accountId, ct);
        return ToAccountDto(account);
    }

    public async Task<TransactionPage> GetHistoryAsync(
        Guid accountId, int limit, string? cursor = null, CancellationToken ct = default)
    {
        // Load the account first so an unknown id gives a 404 rather than an empty page.
        await LoadAccountAsync(accountId, ct);

        var accountNames = await LoadAccountNamesAsync(ct);
        var page = await history.GetPageAsync(new AccountId(accountId), limit, cursor, ct);

        var items = page.Entries
            .Select(entry => ToTransactionDto(entry, accountNames))
            .ToList();

        return new TransactionPage(items, page.NextCursor);
    }

    // ---------------------------------------------------------------------------------
    // Money movements
    // ---------------------------------------------------------------------------------

    public Task<OperationOutcome> DepositAsync(
        Guid accountId, decimal amount, string idempotencyKey, CancellationToken ct = default)
    {
        // Validate up front so bad input fails immediately and is never retried.
        var validatedAmount = Money.FromUsd(amount);
        var fingerprint = CreateRequestFingerprint("deposit", accountId, counterpartyId: null, validatedAmount);

        return RunMoneyMovementAsync(idempotencyKey, fingerprint, async () =>
        {
            var account = await LoadAccountAsync(accountId, ct);
            var entry = account.Deposit(validatedAmount, clock.GetUtcNow());

            return new MoneyMovement(ChangedAccounts: [account], NewEntries: [entry]);
        }, ct);
    }

    public Task<OperationOutcome> WithdrawAsync(
        Guid accountId, decimal amount, string idempotencyKey, CancellationToken ct = default)
    {
        var validatedAmount = Money.FromUsd(amount);
        var fingerprint = CreateRequestFingerprint("withdraw", accountId, counterpartyId: null, validatedAmount);

        return RunMoneyMovementAsync(idempotencyKey, fingerprint, async () =>
        {
            var account = await LoadAccountAsync(accountId, ct);
            var entry = account.Withdraw(validatedAmount, clock.GetUtcNow());

            return new MoneyMovement(ChangedAccounts: [account], NewEntries: [entry]);
        }, ct);
    }

    public Task<OperationOutcome> TransferAsync(
        Guid fromAccountId, Guid toAccountId, decimal amount, string idempotencyKey, CancellationToken ct = default)
    {
        var validatedAmount = Money.FromUsd(amount);

        if (fromAccountId == toAccountId)
        {
            throw new InvalidTransferException("Choose two different accounts to transfer between.");
        }

        var fingerprint = CreateRequestFingerprint("transfer", fromAccountId, toAccountId, validatedAmount);

        return RunMoneyMovementAsync(idempotencyKey, fingerprint, async () =>
        {
            var source = await LoadAccountAsync(fromAccountId, ct);
            var destination = await LoadAccountAsync(toAccountId, ct);
            var transfer = source.TransferTo(destination, validatedAmount, clock.GetUtcNow());

            return new MoneyMovement(
                ChangedAccounts: [source, destination],
                NewEntries: [transfer.Debit, transfer.Credit]);
        }, ct);
    }

    // ---------------------------------------------------------------------------------
    // Running a money movement: retries and idempotency
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// Runs a money movement, retrying if another request changes the same account
    /// between our load and our save. Each retry starts again from fresh data, so the
    /// business rules are always checked against the latest balance.
    ///
    /// Every money movement needs an idempotency key. It's what lets a client safely resend
    /// a request whose response was lost, without the money moving twice.
    /// </summary>
    private async Task<OperationOutcome> RunMoneyMovementAsync(
        string idempotencyKey,
        string requestFingerprint,
        Func<Task<MoneyMovement>> performMovement,
        CancellationToken ct)
    {
        ValidateIdempotencyKey(idempotencyKey);

        var attempt = 1;
        while (true)
        {
            try
            {
                return await RunOnceAsync(idempotencyKey, requestFingerprint, performMovement, ct);
            }
            catch (ConcurrencyConflictException) when (attempt < MaxAttempts)
            {
                // Someone else saved first (a change to one of our accounts, or a request with
                // the same idempotency key). Nothing of ours was written, so just try again.
                attempt++;
            }
        }
    }

    /// <summary>
    /// A single attempt. If the idempotency key has been seen before, the earlier response
    /// is replayed instead of moving money again.
    /// </summary>
    private async Task<OperationOutcome> RunOnceAsync(
        string idempotencyKey,
        string requestFingerprint,
        Func<Task<MoneyMovement>> performMovement,
        CancellationToken ct)
    {
        var previousRequest = await idempotency.FindAsync(idempotencyKey, ct);
        if (previousRequest is not null)
        {
            return ReplayPreviousRequest(previousRequest, requestFingerprint);
        }

        var movement = await performMovement();
        var result = await BuildResultAsync(movement, ct);
        var idempotencyRecord = new IdempotencyRecord(idempotencyKey, requestFingerprint, result, clock.GetUtcNow());

        // The new balances, the ledger entries and the idempotency record are saved in one
        // atomic write, so there's never a moment where money moved but the key wasn't recorded.
        await accounts.SaveAsync(movement.ChangedAccounts, idempotencyRecord, ct);

        return new OperationOutcome(result, Replayed: false);
    }

    private static OperationOutcome ReplayPreviousRequest(IdempotencyRecord previousRequest, string requestFingerprint)
    {
        var isSameRequest = previousRequest.Fingerprint == requestFingerprint;
        if (!isSameRequest)
        {
            throw new IdempotencyKeyReusedException();
        }

        // The same request was sent again (e.g. the client never got our first response).
        // Return exactly what we returned the first time, without moving money again.
        return new OperationOutcome(previousRequest.Result, Replayed: true);
    }

    private static void ValidateIdempotencyKey(string idempotencyKey)
    {
        ArgumentNullException.ThrowIfNull(idempotencyKey);

        var isBlank = string.IsNullOrWhiteSpace(idempotencyKey);
        var isTooLong = idempotencyKey.Length > MaxIdempotencyKeyLength;

        if (isBlank || isTooLong)
        {
            throw new InvalidIdempotencyKeyException(
                $"Idempotency-Key must be 1 to {MaxIdempotencyKeyLength} non-blank characters.");
        }

        // Only simple key characters are allowed. This also catches a client that sent the
        // header twice: those values arrive joined by a comma, and a later retry with a single
        // key would otherwise look like a new request and move the money a second time.
        if (!idempotencyKey.All(IsAllowedKeyCharacter))
        {
            throw new InvalidIdempotencyKeyException(
                "Idempotency-Key may contain only letters, digits and the characters - _ . : "
                + "(a UUID is a good choice). Send exactly one key per request.");
        }
    }

    private static bool IsAllowedKeyCharacter(char character) =>
        char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':';

    /// <summary>
    /// A string describing exactly what was asked for, e.g. "withdraw|{account}||60.00".
    /// A retried request must produce the same fingerprint as the original, otherwise the
    /// idempotency key is being reused for something different.
    /// </summary>
    private static string CreateRequestFingerprint(string operation, Guid accountId, Guid? counterpartyId, Money amount)
    {
        var formattedAmount = amount.Amount.ToString("F2", CultureInfo.InvariantCulture);
        return string.Join('|', operation, accountId, counterpartyId, formattedAmount);
    }

    // ---------------------------------------------------------------------------------
    // Loading and mapping
    // ---------------------------------------------------------------------------------

    private async Task<Account> LoadAccountAsync(Guid accountId, CancellationToken ct)
    {
        var account = await accounts.FindAsync(new AccountId(accountId), ct);
        if (account is null)
        {
            throw new AccountNotFoundException(accountId);
        }

        return account;
    }

    /// <summary>Account names by id, used to label the other side of a transfer.</summary>
    private async Task<IReadOnlyDictionary<AccountId, string>> LoadAccountNamesAsync(CancellationToken ct)
    {
        var allAccounts = await accounts.ListAsync(ct);
        return allAccounts.ToDictionary(account => account.Id, account => account.Name);
    }

    private async Task<OperationResult> BuildResultAsync(MoneyMovement movement, CancellationToken ct)
    {
        var accountNames = await LoadAccountNamesAsync(ct);

        var changedAccounts = movement.ChangedAccounts.Select(ToAccountDto).ToList();
        var newTransactions = movement.NewEntries.Select(entry => ToTransactionDto(entry, accountNames)).ToList();

        return new OperationResult(changedAccounts, newTransactions);
    }

    private static AccountDto ToAccountDto(Account account) => new(
        Id: account.Id.Value,
        Name: account.Name,
        Type: account.Type,
        NumberSuffix: account.NumberSuffix,
        Balance: account.Balance.Amount);

    private static TransactionDto ToTransactionDto(LedgerEntry entry, IReadOnlyDictionary<AccountId, string> accountNames) => new(
        Id: entry.Id,
        AccountId: entry.AccountId.Value,
        Type: entry.Type,
        Amount: entry.Amount.Amount,
        SignedAmount: entry.SignedAmount,
        BalanceAfter: entry.BalanceAfter.Amount,
        OccurredAt: entry.OccurredAt,
        TransferId: entry.TransferId,
        CounterpartyAccountId: entry.CounterpartyAccountId?.Value,
        CounterpartyName: FindAccountName(entry.CounterpartyAccountId, accountNames));

    private static string? FindAccountName(AccountId? accountId, IReadOnlyDictionary<AccountId, string> accountNames)
    {
        if (accountId is null)
        {
            return null;
        }

        return accountNames.TryGetValue(accountId.Value, out var name) ? name : null;
    }
}
