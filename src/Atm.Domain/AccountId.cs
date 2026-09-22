namespace Atm.Domain;

/// <summary>Strongly-typed identifier so account ids can't be confused with other Guids.</summary>
public readonly record struct AccountId(Guid Value)
{
    public static AccountId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString();
}
