using Microsoft.AspNetCore.Diagnostics;

namespace Atm.Api;

/// <summary>
/// Handles requests that fail before a handler is reached, such as a body that isn't valid
/// JSON or a number too large for <see cref="decimal"/>. Model binding runs before endpoint
/// filters, so <see cref="DomainErrorFilter"/> never sees these. Without this, they would be
/// reported as 500s even though the request, not the server, is at fault.
/// </summary>
internal sealed class MalformedRequestHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        if (exception is not BadHttpRequestException badRequest)
        {
            return false; // Not ours: fall through to the generic 500.
        }

        context.Response.StatusCode = badRequest.StatusCode; // 400 for an unreadable request.

        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            Exception = exception,
            ProblemDetails =
            {
                Status = badRequest.StatusCode,
                Title = "Malformed request",
                Detail = "The request couldn't be read. Check that the body is valid JSON and that "
                    + "the amount is a number with at most two decimal places.",
                Extensions = { ["code"] = "malformed_request" },
            },
        });
    }
}
