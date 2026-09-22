namespace Atm.Api;

public sealed record AmountRequest(decimal Amount);

public sealed record TransferRequest(Guid FromAccountId, Guid ToAccountId, decimal Amount);
