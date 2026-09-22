namespace Atm.Application;

public sealed class AccountNotFoundException(Guid accountId)
    : Exception($"Account {accountId} was not found.")
{
    public Guid AccountId { get; } = accountId;
}

/// <summary>Another operation modified the account between load and save.</summary>
public sealed class ConcurrencyConflictException(string message) : Exception(message);

/// <summary>A deposit, withdrawal or transfer arrived without an Idempotency-Key header.</summary>
public sealed class MissingIdempotencyKeyException()
    : Exception("Every deposit, withdrawal and transfer needs an Idempotency-Key header. "
        + "Send a new unique value (such as a UUID) for each new request, and the same value when retrying it.");

public sealed class InvalidIdempotencyKeyException(string message) : Exception(message);

/// <summary>The idempotency key was already used for a different request.</summary>
public sealed class IdempotencyKeyReusedException()
    : Exception("This idempotency key was already used for a different request. Use a new key for each new request.");

public sealed class InvalidCursorException()
    : Exception("The page cursor isn't valid. Start again from the first page.");
