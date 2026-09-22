using Atm.Application;

namespace Atm.Api;

/// <summary>
/// The HTTP API. Each handler just translates the request into a call on
/// <see cref="AtmService"/> and returns the result; there's no logic here.
/// </summary>
public static class AtmEndpoints
{
    public const string IdempotencyKeyHeader = "Idempotency-Key";
    public const string ReplayedHeader = "Idempotent-Replayed";

    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;

    public static IEndpointRouteBuilder MapAtmEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").AddEndpointFilter<DomainErrorFilter>();

        api.MapGet("/accounts", GetAccounts);
        api.MapGet("/accounts/{id:guid}", GetAccount);
        api.MapGet("/accounts/{id:guid}/transactions", GetTransactions);

        api.MapPost("/accounts/{id:guid}/deposits", Deposit);
        api.MapPost("/accounts/{id:guid}/withdrawals", Withdraw);
        api.MapPost("/transfers", Transfer);

        return app;
    }

    // ---------------------------------------------------------------------------------
    // Reads
    // ---------------------------------------------------------------------------------

    private static Task<IReadOnlyList<AccountDto>> GetAccounts(AtmService atm, CancellationToken ct)
    {
        return atm.GetAccountsAsync(ct);
    }

    private static Task<AccountDto> GetAccount(Guid id, AtmService atm, CancellationToken ct)
    {
        return atm.GetAccountAsync(id, ct);
    }

    private static Task<TransactionPage> GetTransactions(
        Guid id,
        int? limit,
        string? cursor,
        AtmService atm,
        CancellationToken ct)
    {
        // Keep page sizes sensible rather than rejecting an out-of-range limit.
        var pageSize = Math.Clamp(limit ?? DefaultPageSize, 1, MaxPageSize);
        return atm.GetHistoryAsync(id, pageSize, cursor, ct);
    }

    // ---------------------------------------------------------------------------------
    // Money movements
    // ---------------------------------------------------------------------------------

    private static async Task<OperationResult> Deposit(
        Guid id,
        AmountRequest body,
        HttpContext http,
        AtmService atm,
        CancellationToken ct)
    {
        var idempotencyKey = RequireIdempotencyKey(http.Request);
        var outcome = await atm.DepositAsync(id, body.Amount, idempotencyKey, ct);
        return MarkIfReplayed(http.Response, outcome);
    }

    private static async Task<OperationResult> Withdraw(
        Guid id,
        AmountRequest body,
        HttpContext http,
        AtmService atm,
        CancellationToken ct)
    {
        var idempotencyKey = RequireIdempotencyKey(http.Request);
        var outcome = await atm.WithdrawAsync(id, body.Amount, idempotencyKey, ct);
        return MarkIfReplayed(http.Response, outcome);
    }

    private static async Task<OperationResult> Transfer(
        TransferRequest body,
        HttpContext http,
        AtmService atm,
        CancellationToken ct)
    {
        var idempotencyKey = RequireIdempotencyKey(http.Request);
        var outcome = await atm.TransferAsync(body.FromAccountId, body.ToAccountId, body.Amount, idempotencyKey, ct);
        return MarkIfReplayed(http.Response, outcome);
    }

    /// <summary>
    /// Money movements must carry an Idempotency-Key header. Without one, a client that
    /// loses a response has no safe way to retry, and could end up withdrawing twice.
    /// </summary>
    private static string RequireIdempotencyKey(HttpRequest request)
    {
        var keys = request.Headers[IdempotencyKeyHeader];

        if (keys.Count == 0)
        {
            throw new MissingIdempotencyKeyException();
        }

        // Several headers would be combined into one comma-separated value. A client that
        // then retried with a single key would look like a different request, and the money
        // would move twice, so reject it instead of guessing which key was meant.
        if (keys.Count > 1)
        {
            throw new InvalidIdempotencyKeyException("Send exactly one Idempotency-Key header.");
        }

        return keys[0] ?? throw new MissingIdempotencyKeyException();
    }

    /// <summary>
    /// A replayed response has exactly the same body as the original. The header is the
    /// only way a client can tell it's a replay.
    /// </summary>
    private static OperationResult MarkIfReplayed(HttpResponse response, OperationOutcome outcome)
    {
        if (outcome.Replayed)
        {
            response.Headers[ReplayedHeader] = "true";
        }

        return outcome.Result;
    }
}
