// Talking to the ATM API.

const MAX_ATTEMPTS = 3;
const RETRY_DELAY_MS = 300;

/** An error from the API, with a message that's safe to show the user. */
export class ApiError extends Error {
  /**
   * @param {string} message
   * @param {boolean} retryable True when sending the same request again might work
   *   (the network dropped, or the server had a problem). False when the server
   *   gave a definite answer, like "insufficient funds".
   */
  constructor(message, retryable) {
    super(message);
    this.name = "ApiError";
    this.retryable = retryable;
  }
}

export function getJson(path) {
  return sendRequest(path, { method: "GET" });
}

async function sendRequest(path, { method, body, headers = {} }) {
  let response;

  try {
    response = await fetch(`/api${path}`, {
      method,
      body,
      headers: {
        "Content-Type": "application/json",
        Accept: "application/json",
        ...headers,
      },
    });
  } catch {
    // fetch only throws when no response arrived at all: offline, connection reset, etc.
    throw new ApiError("Couldn't reach the bank.", true);
  }

  const responseBody = await readJsonOrNull(response);

  if (response.ok) {
    return responseBody;
  }

  // Error responses are problem details: `detail` is the user-facing message.
  const message = responseBody?.detail ?? responseBody?.title ?? `The request failed (${response.status}).`;
  const isServerError = response.status >= 500;
  throw new ApiError(message, isServerError);
}

async function readJsonOrNull(response) {
  try {
    return await response.json();
  } catch {
    return null;
  }
}

// ---------------------------------------------------------------------------
// Money movements, sent safely with an idempotency key
// ---------------------------------------------------------------------------

/**
 * The most recent request we sent but never got a definite answer for (for example,
 * the connection dropped after the server may already have processed it). If the user
 * sends the same request again, we reuse its key so the server applies it at most once.
 *
 * @type {{ fingerprint: string, key: string } | null}
 */
let unconfirmedRequest = null;

/**
 * POSTs a deposit, withdrawal or transfer.
 *
 * Network failures and server errors are retried automatically with the same idempotency
 * key, so a withdrawal is never applied twice even if a response is lost. If every attempt
 * fails, the key is kept: pressing the button again for the same request is still safe.
 *
 * @param {string} path
 * @param {object} payload
 * @param {string} fingerprint Identifies "the same request", e.g. "withdraw|{account}||60.00".
 */
export async function postMoneyMovement(path, payload, fingerprint) {
  const idempotencyKey = idempotencyKeyFor(fingerprint);

  for (let attempt = 1; attempt <= MAX_ATTEMPTS; attempt++) {
    try {
      const result = await sendRequest(path, {
        method: "POST",
        body: JSON.stringify(payload),
        headers: { "Idempotency-Key": idempotencyKey },
      });

      unconfirmedRequest = null; // Definite answer: it worked.
      return result;
    } catch (error) {
      if (!error.retryable) {
        unconfirmedRequest = null; // Definite answer: the server said no.
        throw error;
      }

      const isLastAttempt = attempt === MAX_ATTEMPTS;
      if (isLastAttempt) {
        throw error; // Still no answer. Keep the key so a manual retry is safe.
      }

      await wait(RETRY_DELAY_MS * attempt);
    }
  }
}

function idempotencyKeyFor(fingerprint) {
  const isRetryOfUnconfirmedRequest = unconfirmedRequest?.fingerprint === fingerprint;

  if (!isRetryOfUnconfirmedRequest) {
    unconfirmedRequest = { fingerprint, key: createUniqueKey() };
  }

  return unconfirmedRequest.key;
}

function createUniqueKey() {
  if (crypto.randomUUID) {
    return crypto.randomUUID();
  }

  // Fallback for browsers without randomUUID (only available on HTTPS or localhost).
  const timePart = Date.now().toString(36);
  const randomPart = Math.random().toString(36).slice(2);
  return `${timePart}-${randomPart}`;
}

function wait(milliseconds) {
  return new Promise((resolve) => setTimeout(resolve, milliseconds));
}
