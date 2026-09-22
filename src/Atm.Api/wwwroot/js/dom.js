// The page's elements, and small helpers for building and announcing things.

const byId = (id) => document.getElementById(id);

export const elements = {
  today: byId("today"),

  accountList: byId("accounts"),

  form: byId("action-form"),
  actionRadios: document.querySelectorAll('input[name="action"]'),
  transferRoute: byId("transfer-route"),
  transferRouteText: byId("route-text"),
  swapButton: byId("swap"),
  amountInput: byId("amount"),
  amountHint: byId("amount-hint"),
  amountError: byId("amount-error"),
  quickAmountButtons: document.querySelectorAll("[data-amount]"),
  clearButton: byId("clear"),
  confirmButton: byId("confirm"),
  formStatus: byId("form-status"),

  receiptTitle: byId("receipt-title"),
  receiptBalance: byId("receipt-balance"),
  receiptEntries: byId("entries"),
  showEarlierButton: byId("more"),

  announcer: byId("announcer"),
};

/**
 * Creates an element in one call.
 *
 *   createElement("span", { className: "name", textContent: "Checking" })
 *   createElement("div", { attributes: { "aria-hidden": "true" } }, child1, child2)
 *
 * `attributes` are set with setAttribute (needed for aria-* and role); everything
 * else is assigned as a property.
 */
export function createElement(tagName, { attributes = {}, ...properties } = {}, ...children) {
  const element = document.createElement(tagName);
  Object.assign(element, properties);

  for (const [name, value] of Object.entries(attributes)) {
    element.setAttribute(name, value);
  }

  element.append(...children);
  return element;
}

/**
 * Tells screen reader users about a change that has no visible message of its own,
 * like a quick amount being filled in. Doesn't interrupt what they're currently hearing.
 */
export function announce(message) {
  // Empty the region first, so announcing the same message twice is still read out.
  elements.announcer.textContent = "";

  setTimeout(() => {
    elements.announcer.textContent = message;
  }, 50);
}
