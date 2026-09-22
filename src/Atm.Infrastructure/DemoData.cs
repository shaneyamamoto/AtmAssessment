using Atm.Application;
using Atm.Domain;

namespace Atm.Infrastructure;

/// <summary>Seeds the two accounts the single demo user owns.</summary>
public static class DemoData
{
    public static readonly AccountId CheckingId = new(Guid.Parse("7c1f5a52-3f0e-4d4e-9d7a-2b8a1c4e0001"));
    public static readonly AccountId SavingsId = new(Guid.Parse("7c1f5a52-3f0e-4d4e-9d7a-2b8a1c4e0002"));

    public static async Task SeedAsync(IAccountRepository repository, TimeProvider clock, CancellationToken ct = default)
    {
        if ((await repository.ListAsync(ct)).Count > 0) return;

        var now = clock.GetUtcNow();
        await repository.SaveAsync(
        [
            Account.Open(CheckingId, "Checking", AccountType.Checking, "4821", Money.FromUsd(1_250.00m), now),
            Account.Open(SavingsId, "Savings", AccountType.Savings, "9037", Money.FromUsd(5_000.00m), now),
        ], ct: ct);
    }
}
