using System.Diagnostics.CodeAnalysis;
using Atm.Application;
using Atm.Domain;

namespace Atm.Api;

/// <summary>
/// Turns expected business failures (insufficient funds, an invalid amount, and so on) into
/// problem-details responses with a machine-readable <c>code</c>. Because they're handled
/// here, they aren't logged as server errors. Anything not listed below is unexpected and
/// falls through to the global exception handler as a 500.
/// </summary>
internal sealed class DomainErrorFilter : IEndpointFilter
{
    /// <summary>How one kind of failure is reported to the client.</summary>
    private sealed record ErrorResponse(int StatusCode, string Code, string Title);

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (Exception exception) when (TryGetErrorResponse(exception, out var error))
        {
            return Results.Problem(
                statusCode: error.StatusCode,
                title: error.Title,
                detail: exception.Message, // Written to be safe to show users.
                extensions: new Dictionary<string, object?> { ["code"] = error.Code });
        }
    }

    /// <returns>False for exceptions we don't expect, which should remain 500s.</returns>
    private static bool TryGetErrorResponse(Exception exception, [NotNullWhen(true)] out ErrorResponse? error)
    {
        error = exception switch
        {
            InvalidAmountException =>
                new ErrorResponse(StatusCodes.Status400BadRequest, "invalid_amount", "Invalid amount"),

            InvalidTransferException =>
                new ErrorResponse(StatusCodes.Status400BadRequest, "invalid_transfer", "Invalid transfer"),

            MissingIdempotencyKeyException =>
                new ErrorResponse(StatusCodes.Status400BadRequest, "idempotency_key_required", "Idempotency key required"),

            InvalidIdempotencyKeyException =>
                new ErrorResponse(StatusCodes.Status400BadRequest, "invalid_idempotency_key", "Invalid idempotency key"),

            InvalidCursorException =>
                new ErrorResponse(StatusCodes.Status400BadRequest, "invalid_cursor", "Invalid cursor"),

            AccountNotFoundException =>
                new ErrorResponse(StatusCodes.Status404NotFound, "account_not_found", "Account not found"),

            ConcurrencyConflictException =>
                new ErrorResponse(StatusCodes.Status409Conflict, "concurrency_conflict", "Account busy"),

            InsufficientFundsException =>
                new ErrorResponse(StatusCodes.Status422UnprocessableEntity, "insufficient_funds", "Insufficient funds"),

            IdempotencyKeyReusedException =>
                new ErrorResponse(StatusCodes.Status422UnprocessableEntity, "idempotency_key_reused", "Idempotency key reused"),

            _ => null,
        };

        return error is not null;
    }
}
