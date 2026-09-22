// The ATM screen: what's on it, and what happens when you use it.

import { getJson, postMoneyMovement } from "./api.js";
import { announce, createElement, elements } from "./dom.js";
import {
  TRANSACTION_LIMIT,
  describeTransaction,
  formatFullDate,
  formatMoney,
  formatShortDateTime,
} from "./format.js";

const HISTORY_PAGE_SIZE = 20;

/**
 * What the amount box accepts while typing: up to 5 whole digits and up to 2 decimals.
 * Half-typed values like "12." are allowed.
 */
const TYPED_AMOUNT_PATTERN = /^\d{0,5}(\.\d{0,2})?$/;

/** Wording for each action the user can pick. */
const ACTIONS = {
  deposit: { verb: "Deposit", noun: "deposit" },
  withdraw: { verb: "Withdraw", noun: "withdrawal" },
  transfer: { verb: "Transfer", noun: "transfer" },
};

// ---------------------------------------------------------------------------
// State
// ---------------------------------------------------------------------------

const state = {
  accounts: [],
  selectedAccountId: null,

  /** "deposit", "withdraw" or "transfer" */
  action: "deposit",
  isSubmitting: false,

  /** Loaded history for the selected account, newest first. */
  history: [],
  nextHistoryCursor: null,
  isLoadingEarlierHistory: false,

  /** Transactions to highlight the next time the receipt is drawn. */
  newTransactionIds: new Set(),

  /** The "From X to Y" text currently shown, so we only announce real changes. */
  shownTransferRoute: "",
};

function selectedAccount() {
  return state.accounts.find((account) => account.id === state.selectedAccountId);
}

/** The account that isn't selected. There are exactly two. */
function otherAccount() {
  return state.accounts.find((account) => account.id !== state.selectedAccountId);
}

function findAccount(accountId) {
  return state.accounts.find((account) => account.id === accountId);
}

function enteredAmount() {
  return Number.parseFloat(elements.amountInput.value) || 0;
}

function renderEverything() {
  renderAccountCards();
  renderActionForm();
  renderReceipt();
}

// ---------------------------------------------------------------------------
// Account cards
//
// The cards are built once and then updated in place, so the one with keyboard focus
// is never destroyed. They follow the ARIA radio group pattern: the group is a single
// tab stop, the arrow keys move between cards, and Home/End jump to the first/last.
// ---------------------------------------------------------------------------

function buildAccountCards() {
  const cards = state.accounts.map(createAccountCard);
  elements.accountList.replaceChildren(...cards);
}

function createAccountCard(account) {
  const card = createElement(
    "button",
    { type: "button", className: "account" },
    createElement("span", {
      className: "account-check",
      textContent: "✓",
      attributes: { "aria-hidden": "true" },
    }),
    createElement("span", { className: "account-name", textContent: account.name }),
    createElement("span", { className: "account-number", textContent: `Account ending ${account.numberSuffix}` }),
    createElement("span", { className: "account-balance" }),
  );

  card.dataset.accountId = account.id;
  card.setAttribute("role", "radio");
  card.addEventListener("click", () => selectAccount(account.id));
  card.addEventListener("keydown", handleAccountCardKeydown);

  return card;
}

function allAccountCards() {
  return [...elements.accountList.querySelectorAll(".account")];
}

function renderAccountCards() {
  for (const card of allAccountCards()) {
    const account = findAccount(card.dataset.accountId);
    const isSelected = account.id === state.selectedAccountId;

    card.setAttribute("aria-checked", String(isSelected));

    // Only the selected card is in the tab order ("roving tabindex").
    card.tabIndex = isSelected ? 0 : -1;

    card.querySelector(".account-balance").textContent = formatMoney(account.balance);
  }
}

function handleAccountCardKeydown(event) {
  const cards = allAccountCards();
  const currentIndex = cards.indexOf(event.currentTarget);
  const targetIndex = cardIndexForKey(event.key, currentIndex, cards.length);

  if (targetIndex === null) {
    return; // Not a navigation key.
  }

  event.preventDefault(); // Stop the arrow keys from scrolling the page.

  const targetCard = cards[targetIndex];
  targetCard.focus();
  selectAccount(targetCard.dataset.accountId);
}

/** Which card a key press moves to, wrapping around at both ends. Null for other keys. */
function cardIndexForKey(key, currentIndex, cardCount) {
  switch (key) {
    case "ArrowRight":
    case "ArrowDown":
      return (currentIndex + 1) % cardCount;
    case "ArrowLeft":
    case "ArrowUp":
      return (currentIndex - 1 + cardCount) % cardCount;
    case "Home":
      return 0;
    case "End":
      return cardCount - 1;
    default:
      return null;
  }
}

async function selectAccount(accountId) {
  if (accountId === state.selectedAccountId) {
    return;
  }

  state.selectedAccountId = accountId;
  state.history = [];
  state.nextHistoryCursor = null;

  clearMessages();
  renderAccountCards();
  renderActionForm();

  try {
    await loadFirstHistoryPage();
    renderReceipt();
  } catch (error) {
    showError(error.message);
  }
}

// ---------------------------------------------------------------------------
// Action form: deposit / withdraw / transfer
// ---------------------------------------------------------------------------

function renderActionForm() {
  renderTransferRoute();

  elements.amountHint.textContent = amountHintText();
  elements.confirmButton.textContent = confirmButtonLabel();

  // aria-disabled rather than disabled: a disabled button can't keep focus, so keyboard
  // users would be thrown back to the top of the page after every transaction.
  elements.confirmButton.setAttribute("aria-disabled", String(!canSubmit()));
  elements.form.setAttribute("aria-busy", String(state.isSubmitting));
}

function canSubmit() {
  const hasAccount = selectedAccount() !== undefined;
  const hasAmount = enteredAmount() > 0;
  return hasAccount && hasAmount && !state.isSubmitting;
}

function renderTransferRoute() {
  elements.transferRoute.hidden = state.action !== "transfer";

  const source = selectedAccount();
  const destination = otherAccount();
  if (!source || !destination) {
    return;
  }

  const route = `From ${source.name} to ${destination.name}`;
  if (route === state.shownTransferRoute) {
    return;
  }

  elements.transferRouteText.textContent = route;

  // Announce a change of direction, but not the very first time the text is filled in.
  const isFirstTime = state.shownTransferRoute === "";
  if (state.action === "transfer" && !isFirstTime) {
    announce(`Transferring ${route.toLowerCase()}.`);
  }

  state.shownTransferRoute = route;
}

function amountHintText() {
  const account = selectedAccount();
  if (!account) {
    return "";
  }

  if (state.action === "deposit") {
    return `Deposits up to ${formatMoney(TRANSACTION_LIMIT)} per transaction.`;
  }

  return `Available in ${account.name}: ${formatMoney(account.balance)}`;
}

function confirmButtonLabel() {
  if (state.isSubmitting) {
    return "Processing…";
  }

  const verb = ACTIONS[state.action].verb;
  const amount = enteredAmount();

  return amount > 0 ? `${verb} ${formatMoney(amount)}` : verb;
}

function handleActionChange(event) {
  state.action = event.target.value;
  clearMessages();
  renderActionForm();
}

function handleAmountInput() {
  const input = elements.amountInput;

  if (!TYPED_AMOUNT_PATTERN.test(input.value)) {
    // Undo the keystroke, and tell screen reader users why nothing happened.
    input.value = input.dataset.lastValidValue ?? "";
    announce("Enter numbers only, with up to two decimal places.");
  }

  input.dataset.lastValidValue = input.value;

  if (elements.amountError.textContent) {
    showError(""); // The user is fixing the problem, so stop flagging it.
  }

  renderActionForm();
}

function handleQuickAmountClick(event) {
  const amount = event.currentTarget.dataset.amount;
  setAmountInput(amount);
  showError("");
  renderActionForm();
  announce(`Amount set to ${formatMoney(Number(amount))}.`);
}

/** Clears the amount and messages only. A native form reset would also snap the action back to Deposit. */
function handleClearClick() {
  setAmountInput("");
  clearMessages();
  renderActionForm();
  announce("Amount cleared.");
}

function handleSwapClick() {
  const destination = otherAccount();
  if (destination) {
    selectAccount(destination.id);
  }
}

function setAmountInput(value) {
  elements.amountInput.value = value;
  elements.amountInput.dataset.lastValidValue = value;
}

// ---------------------------------------------------------------------------
// Messages
// ---------------------------------------------------------------------------

/** Progress and success messages. Read out politely by screen readers. */
function showStatus(message) {
  elements.formStatus.textContent = message;
}

/** Errors about the request. Read out immediately, and linked to the amount box. */
function showError(message) {
  elements.amountError.textContent = message;

  const hasError = message !== "";
  elements.amountInput.setAttribute("aria-invalid", String(hasError));
}

function clearMessages() {
  showStatus("");
  showError("");
}

// ---------------------------------------------------------------------------
// Submitting a deposit, withdrawal or transfer
// ---------------------------------------------------------------------------

async function handleSubmit(event) {
  event.preventDefault();

  const account = selectedAccount();
  if (state.isSubmitting || !account) {
    return;
  }

  const amount = enteredAmount();
  if (amount <= 0) {
    showError("Enter an amount greater than zero.");
    elements.amountInput.focus();
    return;
  }

  const request = {
    action: state.action,
    account,
    otherAccount: otherAccount(),
    amount,
  };

  state.isSubmitting = true;
  clearMessages();
  showStatus(`Processing your ${ACTIONS[request.action].noun}…`);
  renderActionForm();

  try {
    const result = await sendMoneyMovement(request);

    updateAccounts(result.accounts);
    for (const transaction of result.transactions) {
      state.newTransactionIds.add(transaction.id);
    }

    await loadFirstHistoryPage();
    setAmountInput("");
    showStatus(successMessage(request));
    // Focus stays on the Confirm button, so the success message is read without interruption.
  } catch (error) {
    showStatus("");
    handleSubmitError(error);
  } finally {
    state.isSubmitting = false;
    renderEverything();
  }
}

function sendMoneyMovement(request) {
  const { action, account, otherAccount, amount } = request;
  const fingerprint = requestFingerprint(request);

  switch (action) {
    case "deposit":
      return postMoneyMovement(`/accounts/${account.id}/deposits`, { amount }, fingerprint);

    case "withdraw":
      return postMoneyMovement(`/accounts/${account.id}/withdrawals`, { amount }, fingerprint);

    case "transfer":
      return postMoneyMovement(
        "/transfers",
        { fromAccountId: account.id, toAccountId: otherAccount.id, amount },
        fingerprint,
      );

    default:
      throw new Error(`Unknown action: ${action}`);
  }
}

/** Identifies "the same request", so a retry can safely reuse the same idempotency key. */
function requestFingerprint({ action, account, otherAccount, amount }) {
  return [action, account.id, otherAccount?.id, amount.toFixed(2)].join("|");
}

/** Merges updated accounts from the server into state. */
function updateAccounts(updatedAccounts) {
  state.accounts = state.accounts.map((account) => {
    const updated = updatedAccounts.find((candidate) => candidate.id === account.id);
    return updated ?? account;
  });
}

function successMessage({ action, account, otherAccount, amount }) {
  const amountText = formatMoney(amount);
  const newBalance = (accountId) => formatMoney(findAccount(accountId).balance);

  switch (action) {
    case "deposit":
      return `Deposited ${amountText} to ${account.name}. New balance ${newBalance(account.id)}.`;

    case "withdraw":
      return `Withdrew ${amountText} from ${account.name}. Take your cash. New balance ${newBalance(account.id)}.`;

    case "transfer":
      return (
        `Transferred ${amountText} from ${account.name} to ${otherAccount.name}. ` +
        `${account.name} balance ${newBalance(account.id)}, ${otherAccount.name} balance ${newBalance(otherAccount.id)}.`
      );

    default:
      return "Done.";
  }
}

function handleSubmitError(error) {
  if (error.retryable) {
    // We don't know whether it went through. Leave focus on Confirm: pressing it
    // again is exactly the safe retry we're suggesting.
    showError(
      `${error.message} Your request may not have gone through. ` +
        "Press the button again to retry; it won't be applied twice.",
    );
    return;
  }

  // The server rejected the amount: put the user back in the box that needs changing.
  showError(error.message);
  elements.amountInput.focus();
}

// ---------------------------------------------------------------------------
// Receipt: transaction history
// ---------------------------------------------------------------------------

function renderReceipt() {
  const account = selectedAccount();
  if (!account) {
    return;
  }

  renderReceiptHeader(account);
  renderShowEarlierButton();
  renderReceiptEntries();
}

function renderReceiptHeader(account) {
  // The visually hidden prefix gives screen readers a clearer heading than the receipt shows.
  elements.receiptTitle.replaceChildren(
    createElement("span", { className: "visually-hidden", textContent: "Transaction history for " }),
    `${account.name} ending ${account.numberSuffix}`,
  );

  elements.receiptBalance.textContent = `Balance ${formatMoney(account.balance)}`;
}

function renderShowEarlierButton() {
  const button = elements.showEarlierButton;
  const hasEarlierHistory = state.nextHistoryCursor !== null;

  button.hidden = !hasEarlierHistory;
  button.setAttribute("aria-disabled", String(state.isLoadingEarlierHistory));
  button.textContent = state.isLoadingEarlierHistory ? "Loading…" : "Show earlier transactions";
}

function renderReceiptEntries() {
  if (state.history.length === 0) {
    const emptyMessage = createElement("li", {
      className: "empty",
      textContent: "No transactions yet. Make a deposit to get started.",
    });
    elements.receiptEntries.replaceChildren(emptyMessage);
  } else {
    const entries = state.history.map(createReceiptEntry);
    elements.receiptEntries.replaceChildren(...entries);
  }

  // Highlights are one-off: they show on the render right after the transaction.
  state.newTransactionIds.clear();
}

function createReceiptEntry(transaction) {
  const isCredit = transaction.signedAmount > 0;
  const description = describeTransaction(transaction);
  const amount = formatMoney(Math.abs(transaction.signedAmount));
  const signedAmount = `${isCredit ? "+" : "−"}${amount}`;
  const dateTime = formatShortDateTime(transaction.occurredAt);
  const balanceAfter = formatMoney(transaction.balanceAfter);

  // Screen readers hear this one full sentence. The abbreviated rows below are hidden from them.
  const spokenSummary =
    `${description}, ${isCredit ? "plus" : "minus"} ${amount}, ${dateTime}. ` +
    `Balance after: ${balanceAfter}.`;

  const isNew = state.newTransactionIds.has(transaction.id);

  const entry = createElement(
    "li",
    {
      className: isNew ? "entry fresh" : "entry",
      tabIndex: -1, // Focusable from script (see loadEarlierHistory), but not a tab stop.
    },
    createElement("span", { className: "visually-hidden", textContent: spokenSummary }),
    createElement(
      "div",
      { className: "entry-row", attributes: { "aria-hidden": "true" } },
      createElement("span", { textContent: description }),
      createElement("span", {
        className: isCredit ? "entry-amount credit" : "entry-amount",
        textContent: signedAmount,
      }),
    ),
    createElement(
      "div",
      { className: "entry-row", attributes: { "aria-hidden": "true" } },
      createElement("time", {
        className: "entry-when",
        dateTime: transaction.occurredAt,
        textContent: dateTime,
      }),
      createElement("span", { className: "entry-after", textContent: `Bal ${balanceAfter}` }),
    ),
  );

  entry.dataset.transactionId = transaction.id;
  return entry;
}

async function loadFirstHistoryPage() {
  const page = await getJson(`/accounts/${state.selectedAccountId}/transactions?limit=${HISTORY_PAGE_SIZE}`);
  state.history = page.items;
  state.nextHistoryCursor = page.nextCursor;
}

async function loadEarlierHistory() {
  if (state.nextHistoryCursor === null || state.isLoadingEarlierHistory) {
    return;
  }

  const accountId = state.selectedAccountId;
  const buttonHadFocus = document.activeElement === elements.showEarlierButton;
  let loadedTransactions = null;

  state.isLoadingEarlierHistory = true;
  renderReceipt();

  try {
    const cursor = encodeURIComponent(state.nextHistoryCursor);
    const page = await getJson(`/accounts/${accountId}/transactions?limit=${HISTORY_PAGE_SIZE}&cursor=${cursor}`);

    const userSwitchedAccounts = accountId !== state.selectedAccountId;
    if (!userSwitchedAccounts) {
      state.history = state.history.concat(page.items);
      state.nextHistoryCursor = page.nextCursor;
      loadedTransactions = page.items;
    }
  } catch (error) {
    announce(`Couldn't load earlier transactions. ${error.message}`);
  } finally {
    state.isLoadingEarlierHistory = false;
    renderReceipt();
  }

  if (loadedTransactions === null) {
    return;
  }

  // Announcements and focus changes come after the final render above, because
  // rendering replaces the entry elements.
  announceLoadedHistory(loadedTransactions.length);

  // On the last page the button disappears. If it had focus, move focus to the first
  // newly loaded entry, so keyboard users don't get thrown back to the top of the page.
  const reachedStartOfHistory = state.nextHistoryCursor === null;
  if (buttonHadFocus && reachedStartOfHistory && loadedTransactions.length > 0) {
    const firstNewId = loadedTransactions[0].id;
    elements.receiptEntries.querySelector(`[data-transaction-id="${firstNewId}"]`)?.focus();
  }
}

function announceLoadedHistory(count) {
  const noun = count === 1 ? "transaction" : "transactions";
  const reachedStartOfHistory = state.nextHistoryCursor === null;
  const ending = reachedStartOfHistory ? " This is the start of the history." : "";

  announce(`Loaded ${count} earlier ${noun}.${ending}`);
}

function handleShowEarlierClick() {
  const isBusy = elements.showEarlierButton.getAttribute("aria-disabled") === "true";
  if (!isBusy) {
    loadEarlierHistory();
  }
}

// ---------------------------------------------------------------------------
// Start-up
// ---------------------------------------------------------------------------

function attachEventHandlers() {
  elements.form.addEventListener("submit", handleSubmit);
  elements.amountInput.addEventListener("input", handleAmountInput);
  elements.clearButton.addEventListener("click", handleClearClick);
  elements.swapButton.addEventListener("click", handleSwapClick);
  elements.showEarlierButton.addEventListener("click", handleShowEarlierClick);

  for (const radio of elements.actionRadios) {
    radio.addEventListener("change", handleActionChange);
  }

  for (const button of elements.quickAmountButtons) {
    button.addEventListener("click", handleQuickAmountClick);
  }
}

async function start() {
  elements.today.textContent = formatFullDate(new Date());
  attachEventHandlers();

  try {
    state.accounts = await getJson("/accounts");
    state.selectedAccountId = state.accounts[0]?.id ?? null;

    buildAccountCards();

    if (state.selectedAccountId !== null) {
      await loadFirstHistoryPage();
    }

    renderEverything();
  } catch (error) {
    const message = createElement("p", {
      className: "loading",
      textContent: `Couldn't load accounts. ${error.message} Refresh to try again.`,
      attributes: { role: "alert" },
    });
    elements.accountList.replaceChildren(message);
  }
}

start();
