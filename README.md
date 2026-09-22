# Pencheck Assessment ATM

A web-based ATM for a single user with two accounts (checking and savings). Supports deposits, withdrawals and transfers between accounts, with a full transaction history for each.

## Features

- Deposit, withdraw and transfer between checking and savings
- Paginated transaction history for each account
- Atomic, concurrency-safe balance updates
- Idempotent money movements, safe to retry after a lost response
- Consistent, machine-readable error responses
- Accessible UI (keyboard, screen reader and high-contrast support)

## Tech stack

- **Backend:** .NET 8, ASP.NET Core minimal API
- **Frontend:** HTML, CSS and JavaScript modules (no framework or build step)
- **Storage:** in-memory
- **Tests:** xUnit, `WebApplicationFactory`

## Getting started

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

### Run

```bash
dotnet run --project src/Atm.Api
```

Then open http://localhost:5080. The app starts with $1,250 in checking and $5,000 in savings. Data resets on restart.

### Test

```bash
dotnet test
```

## Project structure

```
src/
  Atm.Domain           Account, Money and ledger entries. No dependencies.
  Atm.Application      AtmService (use cases) and the interfaces it depends on.
  Atm.Infrastructure   In-memory store, history cursors and seed data.
  Atm.Api              Endpoints, error mapping, dependency injection and the frontend (wwwroot).
tests/
  Atm.Tests            Unit, service and API tests.
```

Dependencies point inward (Api → Application/Infrastructure → Domain), enforced by project references. The solution is kept to four projects to match the size of the application. `AtmService` depends only on interfaces, so the storage implementation can be replaced without changing it.

## Architecture

### Domain model

`Account` owns its balance, and all changes go through a private `Credit`/`Debit` pair that enforces the business rules:

- Amounts must be greater than zero and no more than $10,000 per transaction.
- A debit cannot take the balance below zero.
- Every change is recorded as a `LedgerEntry`, including the resulting balance.

`Money` wraps `decimal` for exact arithmetic and rejects negative values and fractions of a cent. Amounts outside the range of `decimal`, and values such as `NaN`, are rejected during model binding and returned as `400 malformed_request`. Transfers debit the source account before crediting the destination, so a failed transfer leaves both accounts unchanged.

`Account.Rehydrate` rebuilds an account from stored data without creating a ledger entry. It is `internal` and exposed only to the Infrastructure and test projects through `InternalsVisibleTo`, and a test fails if it is made public. As a result, the domain project file references the Infrastructure project by name.

### Consistency and concurrency

- Each operation saves every account it changes in a single all-or-nothing write.
- Accounts carry a version number. If an account changes between load and save, the save is rejected and `AtmService` retries with fresh data, up to three attempts. This prevents two simultaneous withdrawals from spending the same funds.
- The ledger always sums to the account balance. A test verifies this after 200 parallel withdrawals.

### Idempotency

Every deposit, withdrawal and transfer requires an `Idempotency-Key` header, so that a request whose response was lost can be retried without moving money twice.

| Request | Result |
|---|---|
| New key | Processed normally |
| Same key, same request | Original response replayed with `Idempotent-Replayed: true`; no money moves |
| Same key, different request | `422 idempotency_key_reused` |
| No key | `400 idempotency_key_required`; nothing is processed |

- The key record is written in the same atomic save as the balance change.
- Concurrent requests with the same key are resolved by the concurrency check: one succeeds, and the others replay its response.
- Failed requests are not recorded, so a retry is evaluated against the current balance.
- Keys expire after 24 hours.
- `AtmService` takes the key as a required, non-nullable parameter, so no code path can skip it.
- A request must carry exactly one key, made up of letters, digits and `- _ . :` (a UUID is a good choice). Duplicate headers are combined into a comma-separated value by the HTTP stack, and accepting that would let a later retry with a single key look like a new request.

The frontend retries network and server errors with the same key, and keeps the key if every attempt fails, so a manual retry is also safe.

### Transaction history

History is read separately from the account aggregate and uses cursor-based pagination rather than offsets, so new transactions arriving during pagination do not cause duplicates or gaps. Cursors are opaque, versioned base64 strings.

## API reference

### Endpoints

| Method | Route | Description |
|---|---|---|
| GET | `/api/accounts` | List both accounts |
| GET | `/api/accounts/{id}` | Get one account |
| GET | `/api/accounts/{id}/transactions?limit=20&cursor=…` | Transaction history, newest first. Returns `{ items, nextCursor }`. Limit 1–100 |
| POST | `/api/accounts/{id}/deposits` | Body: `{ "amount": 100.00 }` |
| POST | `/api/accounts/{id}/withdrawals` | Body: `{ "amount": 60.00 }` |
| POST | `/api/transfers` | Body: `{ "fromAccountId", "toAccountId", "amount" }` |

All POST endpoints require an `Idempotency-Key` header and return the updated accounts and the new ledger entries.

### Example

```bash
curl -X POST http://localhost:5080/api/accounts/7c1f5a52-3f0e-4d4e-9d7a-2b8a1c4e0001/withdrawals \
  -H 'Content-Type: application/json' \
  -H 'Idempotency-Key: 3f2b8c1e-...' \
  -d '{"amount": 60}'
```

### Errors

Errors use the [problem details](https://www.rfc-editor.org/rfc/rfc9457) format, with an additional `code` field. The `detail` message is suitable for display to users.

| Status | Code | Cause |
|---|---|---|
| 400 | `malformed_request` | Body isn't valid JSON, or the amount isn't a number `decimal` can hold |
| 400 | `invalid_amount` | Zero, negative, fractions of a cent, or over $10,000 |
| 400 | `invalid_transfer` | Source and destination are the same account |
| 400 | `idempotency_key_required` | Missing `Idempotency-Key` header |
| 400 | `invalid_idempotency_key` | Key is blank, longer than 100 characters, contains characters other than letters, digits and `- _ . :`, or was sent more than once |
| 400 | `invalid_cursor` | Cursor is malformed |
| 404 | `account_not_found` | Unknown account ID |
| 409 | `concurrency_conflict` | Conflict persisted after three attempts |
| 422 | `insufficient_funds` | Balance too low for the withdrawal or transfer |
| 422 | `idempotency_key_reused` | Key already used for a different request |

Business rule failures are raised as domain exceptions and mapped to these responses by an endpoint filter, so they are not logged as server errors.

## Frontend

The UI is served from `src/Atm.Api/wwwroot` and is split into four JavaScript modules:

| Module | Responsibility |
|---|---|
| `format.js` | Formatting money, dates and transaction labels |
| `api.js` | Server requests and idempotent retries |
| `dom.js` | Element lookups and DOM helpers |
| `app.js` | State, rendering and event handling |

Server data is always inserted as text, never as HTML. All business rules are enforced by the server; the client only restricts input in the amount field. The selected account is the source of each operation, and "Swap direction" reverses it for transfers.

### Accessibility

- Full keyboard support, including a skip link and arrow-key navigation between accounts
- Focus is preserved after every action
- Errors are linked to the amount field, and state changes are announced to screen readers
- Transaction entries are read as complete sentences
- No information is conveyed by color alone
- Dedicated Windows High Contrast (forced colors) styles
- WCAG AA text contrast and support for reduced motion

Automated checks with axe-core and a scripted keyboard walkthrough pass. Testing with NVDA and VoiceOver has not yet been performed.

## Testing

The test suite covers:

- **Domain:** amount validation, deposits, withdrawals, overdraft rejection, transfers and the transaction limit
- **Storage:** atomic saves, version conflicts, pagination and idempotency key expiry
- **Service:** end-to-end use cases, including 200 parallel withdrawals
- **Idempotency:** replays, reused and missing keys, and 20 concurrent requests sharing one key
- **API:** status codes, error codes and headers, via `WebApplicationFactory`

Tests use a controllable `TimeProvider`, so time-dependent behavior such as key expiry is tested without delays.

## Design trade-offs

| Decision | Benefit | Cost |
|---|---|---|
| In-memory storage | No setup required | Data is lost on restart; the lock that makes saves atomic only works within a single process, so multiple instances would break concurrency and idempotency |
| Stored balance plus ledger | Balance reads are instant | The two must stay in sync; all changes go through `Account`, and a test verifies they match |
| Optimistic concurrency | No locks held during processing | Under heavy contention, a request can exhaust its retries and receive a 409 |
| Failed requests not stored against their key | Retries are evaluated against the current balance | The same key can produce different results over time |
| Exceptions for business failures | Simple, centralized mapping to HTTP responses | Failures are not visible in method signatures; a `Result<T>` type would make them explicit |

## Roadmap

1. **SQL Server persistence** behind the existing interfaces, using a `rowversion` column for concurrency, a unique constraint on idempotency keys, and one transaction per operation.
2. **Logging of business failures** at Information level, and at Warning when retries are exhausted, to provide an audit trail of failed attempts.
3. **Browser tests and CI**, adding the Playwright checks for keyboard navigation, lost-response retries and pagination, and running all tests on every push.
4. **Authentication and account ownership**, with ownership checked on every request and idempotency keys scoped per user.
5. **Additional ATM rules**, such as a daily withdrawal limit and dispensing cash in $20 multiples.
