using System.Net;
using System.Text;
using System.Net.Http.Json;
using System.Text.Json;
using Atm.Domain;
using Atm.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Atm.Tests;

public class ApiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client = factory.CreateClient();
    private static readonly Guid Checking = DemoData.CheckingId.Value;
    private static readonly Guid Savings = DemoData.SavingsId.Value;

    /// <summary>POSTs JSON with a fresh Idempotency-Key, which every money movement needs.</summary>
    private Task<HttpResponseMessage> PostWithKeyAsync(string url, object body) =>
        _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, url)
        {
            Headers = { { "Idempotency-Key", Guid.NewGuid().ToString() } },
            Content = JsonContent.Create(body),
        });

    [Fact]
    public async Task Lists_both_accounts()
    {
        var accounts = await _client.GetFromJsonAsync<JsonElement>("/api/accounts");
        Assert.Equal(2, accounts.GetArrayLength());
    }

    [Fact]
    public async Task Overdraft_returns_422_problem_with_code()
    {
        // Other tests in this class share the server and change balances, so read the current one.
        // One cent more than the balance is an overdraft but stays within the per-transaction limit.
        var account = await _client.GetFromJsonAsync<JsonElement>($"/api/accounts/{Checking}");
        var overdraft = account.GetProperty("balance").GetDecimal() + 0.01m;
        Assert.True(overdraft <= Account.TransactionLimit.Amount, "Test precondition: stay within the ATM limit.");

        var response = await PostWithKeyAsync($"/api/accounts/{Checking}/withdrawals", new { amount = overdraft });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("insufficient_funds", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Amount_over_the_ATM_limit_returns_400_even_when_it_would_also_overdraw()
    {
        var overLimit = Account.TransactionLimit.Amount + 0.01m;
        var response = await PostWithKeyAsync($"/api/accounts/{Checking}/withdrawals", new { amount = overLimit });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_amount", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Invalid_amount_returns_400()
    {
        var response = await PostWithKeyAsync($"/api/accounts/{Checking}/deposits", new { amount = 1.005 });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_account_returns_404()
    {
        var response = await _client.GetAsync($"/api/accounts/{Guid.NewGuid()}/transactions");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Transfer_returns_both_updated_accounts()
    {
        var response = await PostWithKeyAsync("/api/transfers",
            new { fromAccountId = Savings, toAccountId = Checking, amount = 1 });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, body.GetProperty("accounts").GetArrayLength());
        Assert.Equal(2, body.GetProperty("transactions").GetArrayLength());
    }

    [Fact]
    public async Task Retry_with_same_idempotency_key_is_replayed_with_header()
    {
        var key = Guid.NewGuid().ToString();
        HttpRequestMessage Request() => new(HttpMethod.Post, $"/api/accounts/{Checking}/deposits")
        {
            Headers = { { "Idempotency-Key", key } },
            Content = JsonContent.Create(new { amount = 7 }),
        };

        var first = await _client.SendAsync(Request());
        var retry = await _client.SendAsync(Request());

        Assert.False(first.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal("true", Assert.Single(retry.Headers.GetValues("Idempotent-Replayed")));
        Assert.Equal(await first.Content.ReadAsStringAsync(), await retry.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Reused_idempotency_key_returns_422()
    {
        var key = Guid.NewGuid().ToString();
        async Task<HttpResponseMessage> Deposit(int amount) => await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, $"/api/accounts/{Checking}/deposits")
            {
                Headers = { { "Idempotency-Key", key } },
                Content = JsonContent.Create(new { amount }),
            });

        await Deposit(1);
        var response = await Deposit(2);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("idempotency_key_reused", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task History_is_paged_with_a_cursor()
    {
        for (var i = 0; i < 3; i++)
            await PostWithKeyAsync($"/api/accounts/{Savings}/deposits", new { amount = 1 });

        var first = await _client.GetFromJsonAsync<JsonElement>($"/api/accounts/{Savings}/transactions?limit=2");
        var cursor = first.GetProperty("nextCursor").GetString();
        var second = await _client.GetFromJsonAsync<JsonElement>(
            $"/api/accounts/{Savings}/transactions?limit=2&cursor={Uri.EscapeDataString(cursor!)}");

        Assert.Equal(2, first.GetProperty("items").GetArrayLength());
        Assert.True(second.GetProperty("items").GetArrayLength() > 0);
    }

    [Fact]
    public async Task Invalid_cursor_returns_400()
    {
        var response = await _client.GetAsync($"/api/accounts/{Checking}/transactions?cursor=not-a-cursor");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("deposits")]
    [InlineData("withdrawals")]
    public async Task Money_movement_without_an_idempotency_key_is_rejected(string operation)
    {
        var balanceBefore = (await _client.GetFromJsonAsync<JsonElement>($"/api/accounts/{Checking}"))
            .GetProperty("balance").GetDecimal();

        var response = await _client.PostAsJsonAsync($"/api/accounts/{Checking}/{operation}", new { amount = 5 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("idempotency_key_required", problem.GetProperty("code").GetString());

        var balanceAfter = (await _client.GetFromJsonAsync<JsonElement>($"/api/accounts/{Checking}"))
            .GetProperty("balance").GetDecimal();
        Assert.Equal(balanceBefore, balanceAfter);
    }

    [Fact]
    public async Task Transfer_without_an_idempotency_key_is_rejected()
    {
        var response = await _client.PostAsJsonAsync("/api/transfers",
            new { fromAccountId = Savings, toAccountId = Checking, amount = 1 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("1e30")]      // Larger than decimal can hold
    [InlineData("-1e30")]
    [InlineData("1e400")]     // Larger than double can hold
    [InlineData("NaN")]
    [InlineData("\"sixty\"")] // Not a number at all
    public async Task Unreadable_amount_returns_400_rather_than_500(string rawAmount)
    {
        // These fail during model binding, before any endpoint filter runs, so they need
        // handling separately from business rule failures.
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/accounts/{Checking}/deposits")
        {
            Headers = { { "Idempotency-Key", Guid.NewGuid().ToString() } },
            Content = new StringContent($$"""{"amount": {{rawAmount}}}""", Encoding.UTF8, "application/json"),
        };

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("malformed_request", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Malformed_json_returns_400()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/accounts/{Checking}/deposits")
        {
            Headers = { { "Idempotency-Key", Guid.NewGuid().ToString() } },
            Content = new StringContent("""{"amount":""", Encoding.UTF8, "application/json"),
        };

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Request_with_two_idempotency_keys_is_rejected()
    {
        // Several headers are combined into one comma-separated value. A later retry sending a
        // single key would look like a new request, so the request is rejected instead.
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/accounts/{Checking}/deposits")
        {
            Headers = { { "Idempotency-Key", new[] { "first-key", "second-key" } } },
            Content = JsonContent.Create(new { amount = 1 }),
        };

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_idempotency_key", problem.GetProperty("code").GetString());
    }
}
