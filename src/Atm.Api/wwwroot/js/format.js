// Turning numbers, dates and transactions into text for the screen.

/** Largest single transaction the ATM accepts. Mirrors Account.TransactionLimit on the server. */
export const TRANSACTION_LIMIT = 10_000;

const currencyFormatter = new Intl.NumberFormat("en-US", {
  style: "currency",
  currency: "USD",
});

const shortDateTimeFormatter = new Intl.DateTimeFormat("en-US", {
  month: "short",
  day: "numeric",
  hour: "numeric",
  minute: "2-digit",
});

const fullDateFormatter = new Intl.DateTimeFormat("en-US", { dateStyle: "full" });

/** 1234.5 → "$1,234.50" */
export function formatMoney(amount) {
  return currencyFormatter.format(amount);
}

/** An ISO timestamp → "Sep 21, 2:05 PM" in the viewer's time zone. */
export function formatShortDateTime(isoTimestamp) {
  return shortDateTimeFormatter.format(new Date(isoTimestamp));
}

/** A Date → "Monday, September 21, 2026" */
export function formatFullDate(date) {
  return fullDateFormatter.format(date);
}

/** A short label for a transaction, e.g. "Cash withdrawal" or "Transfer to Savings". */
export function describeTransaction(transaction) {
  const otherAccountName = transaction.counterpartyName ?? "account";

  switch (transaction.type) {
    case "OpeningBalance":
      return "Opening balance";
    case "Deposit":
      return "Deposit";
    case "Withdrawal":
      return "Cash withdrawal";
    case "TransferIn":
      return `Transfer from ${otherAccountName}`;
    case "TransferOut":
      return `Transfer to ${otherAccountName}`;
    default:
      return transaction.type;
  }
}
