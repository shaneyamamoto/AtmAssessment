using System.Text.Json.Serialization;
using Atm.Api;
using Atm.Application;
using Atm.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// Composition root: the only place that knows which adapters implement the ports.
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(sp => new InMemoryAccountStore(sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<IAccountRepository>(sp => sp.GetRequiredService<InMemoryAccountStore>());
builder.Services.AddSingleton<ITransactionHistory>(sp => sp.GetRequiredService<InMemoryAccountStore>());
builder.Services.AddSingleton<IIdempotencyStore>(sp => sp.GetRequiredService<InMemoryAccountStore>());
builder.Services.AddScoped<AtmService>();

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<MalformedRequestHandler>();
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();

await DemoData.SeedAsync(
    app.Services.GetRequiredService<IAccountRepository>(),
    app.Services.GetRequiredService<TimeProvider>());

app.UseExceptionHandler();   // unexpected errors -> generic 500 problem details
app.UseStatusCodePages();    // e.g. malformed routes -> problem details
app.UseDefaultFiles();
app.UseStaticFiles();        // serves the frontend from wwwroot

app.MapAtmEndpoints();

app.Run();

public partial class Program; // exposed for WebApplicationFactory-based tests
