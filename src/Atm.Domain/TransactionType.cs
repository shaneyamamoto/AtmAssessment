namespace Atm.Domain;

public enum TransactionType
{
    OpeningBalance,
    Deposit,
    Withdrawal,
    TransferIn,
    TransferOut,
}

public static class TransactionTypeExtensions
{
    /// <summary>True when the entry adds money to the account.</summary>
    public static bool IsCredit(this TransactionType type) =>
        type is TransactionType.OpeningBalance or TransactionType.Deposit or TransactionType.TransferIn;
}
