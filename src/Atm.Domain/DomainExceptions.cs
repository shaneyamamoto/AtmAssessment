namespace Atm.Domain;

/// <summary>A business rule was violated. Messages are safe to show to the user.</summary>
public abstract class DomainException(string message) : Exception(message);

public sealed class InvalidAmountException(string message) : DomainException(message);

public sealed class InvalidTransferException(string message) : DomainException(message);

public sealed class InsufficientFundsException(AccountId accountId, Money available, Money requested)
    : DomainException($"Insufficient funds. Available balance is {available}.")
{
    public AccountId AccountId { get; } = accountId;
    public Money Available { get; } = available;
    public Money Requested { get; } = requested;
}
