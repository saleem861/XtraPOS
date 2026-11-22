import type { CompletedSale } from "../api/checkout";
import type { CartDraft } from "../cart/model";
import { cartTotals, originalCartTotal } from "../cart/model";
import { addDecimals, compareDecimals, formatPosMoney, isPositiveDecimal, subtractDecimals } from "../cart/decimal";

// The POS window and the customer display window run on the same computer and talk over this channel.
// Only what the customer may see goes on it: no cost, profit, debt balance, or credit limit.
export const customerDisplayChannel = "xtrapos-customer-display";
export const customerDisplayPath = "/customer-display";
export const THANKS_SECONDS = 20;

export interface DisplayLine {
  id: string;
  name: string;
  quantity: string;
  unitPrice: string;
  lineTotal: string;
}

export type CustomerDisplayState =
  | { phase: "idle"; business: string; branch: string; memberQr: string }
  | { phase: "sale"; business: string; branch: string; customerName: string | null; lines: DisplayLine[]; lastLineId: string; pieces: string; beforeDiscount: string; discount: string; total: string }
  | { phase: "payment"; business: string; branch: string; customerName: string | null; total: string; paid: string; change: string; method: "Cash" | "Card" | "Split" }
  | { phase: "deferred"; business: string; branch: string; customerName: string | null; total: string; deferred: string; paidNow: string }
  | { phase: "thanks"; business: string; branch: string; saleNumber: number; change: string; qr: string; qrLabel: string; at: number };

export interface DisplayInput {
  business: string;
  branch: string;
  cart: CartDraft | null;
  customerName: string | null;
  paying: boolean;
  payMethod: "Cash" | "Card" | "Split";
  tendered: string;
  cashPart: string;
  creditOpen: boolean;
  deferred: string;
  receipt: CompletedSale | null;
  receiptAt: number;
  memberQr: string;
  invoiceQr: string;
}

function isAmount(value: string | null | undefined): value is string {
  try {
    return value != null && isPositiveDecimal(value);
  } catch {
    return false;
  }
}

// Always two decimals on the customer screen: 21 → 21.00.
function money(value: string) {
  const rounded = formatPosMoney(isAmount(value) ? value : "0");
  const [whole, fraction = ""] = rounded.split(".");
  return `${whole}.${(fraction + "00").slice(0, 2)}`;
}

function positive(value: string | null | undefined) {
  return isAmount(value) ? value : "0";
}

export function buildDisplayState(input: DisplayInput): CustomerDisplayState {
  const { business, branch, cart } = input;
  const lines = cart?.lines ?? [];
  if (cart && lines.length > 0) {
    const total = cartTotals(cart).total;
    if (input.creditOpen) {
      const deferred = isAmount(input.deferred) && compareDecimals(input.deferred, total) <= 0 ? input.deferred : total;
      return { phase: "deferred", business, branch, customerName: input.customerName, total: money(total), deferred: money(deferred), paidNow: money(subtractDecimals(total, deferred)) };
    }
    if (input.paying) {
      if (input.payMethod === "Card") {
        return { phase: "payment", business, branch, customerName: input.customerName, total: money(total), paid: money(total), change: "0.00", method: "Card" };
      }
      if (input.payMethod === "Split") {
        const cash = positive(input.cashPart);
        const handed = positive(input.tendered);
        const change = compareDecimals(handed, cash) > 0 ? subtractDecimals(handed, cash) : "0";
        return { phase: "payment", business, branch, customerName: input.customerName, total: money(total), paid: money(addDecimals(handed, subtractDecimals(total, cash))), change: money(change), method: "Split" };
      }
      const handed = positive(input.tendered);
      const change = compareDecimals(handed, total) > 0 ? subtractDecimals(handed, total) : "0";
      return { phase: "payment", business, branch, customerName: input.customerName, total: money(total), paid: money(handed), change: money(change), method: "Cash" };
    }
    const before = originalCartTotal(cart);
    const discount = compareDecimals(before, total) > 0 ? subtractDecimals(before, total) : "0";
    const pieces = lines.reduce((sum, line) => line.lineType === "Fixed" ? addDecimals(sum, line.quantity) : addDecimals(sum, "1"), "0");
    return {
      phase: "sale",
      business,
      branch,
      customerName: input.customerName,
      lines: lines.map((line) => line.lineType === "Fixed"
        ? { id: line.cartLineId, name: line.itemName, quantity: line.quantity, unitPrice: money(line.unitPrice), lineTotal: money(line.lineTotal) }
        : { id: line.cartLineId, name: line.referenceItemName, quantity: line.quantity, unitPrice: money(line.unitPrice), lineTotal: money(line.lineTotal) }),
      lastLineId: lines[lines.length - 1].cartLineId,
      pieces,
      beforeDiscount: money(compareDecimals(before, total) > 0 ? before : total),
      discount: money(discount),
      total: money(total),
    };
  }
  if (input.receipt && Date.now() - input.receiptAt < THANKS_SECONDS * 1000) {
    const change = input.receipt.payments.reduce((sum, payment) => addDecimals(sum, positive(payment.changeAmount)), "0");
    const qr = input.invoiceQr || input.memberQr;
    return {
      phase: "thanks",
      business,
      branch,
      saleNumber: input.receipt.saleNumber,
      change: money(change),
      qr,
      qrLabel: input.invoiceQr ? "امسح الرمز لفتح فاتورتك على هاتفك" : input.memberQr ? "امسح الرمز للانضمام إلى عضوية المحل" : "",
      at: input.receiptAt,
    };
  }
  return { phase: "idle", business, branch, memberQr: input.memberQr };
}

// Opens the customer display, on the second screen when Chrome may place windows there,
// sized to that whole screen. The display page itself then asks for full screen.
export async function openCustomerDisplay(): Promise<boolean> {
  const url = `${window.location.origin}${customerDisplayPath}`;
  let features = `popup=yes,left=0,top=0,width=${window.screen.availWidth},height=${window.screen.availHeight}`;
  try {
    const details = await (window as unknown as { getScreenDetails?: () => Promise<{ screens: ScreenArea[]; currentScreen: ScreenArea }> }).getScreenDetails?.();
    const other = details?.screens.find((screen) => screen !== details.currentScreen);
    if (other) features = `popup=yes,left=${other.availLeft},top=${other.availTop},width=${other.availWidth},height=${other.availHeight}`;
  } catch {
    // Without the window-placement permission the display opens here and can be dragged to the second screen.
  }
  const opened = window.open(url, "xtrapos-customer-display", features);
  opened?.focus();
  return opened != null;
}

// Asks any open customer display to answer; resolves true if one answers in time.
export function isCustomerDisplayOpen(timeoutMs = 800): Promise<boolean> {
  if (typeof BroadcastChannel !== "function") return Promise.resolve(false);
  return new Promise((resolve) => {
    const channel = new BroadcastChannel(customerDisplayChannel);
    const done = (open: boolean) => {
      window.clearTimeout(timer);
      channel.close();
      resolve(open);
    };
    const timer = window.setTimeout(() => done(false), timeoutMs);
    channel.onmessage = (event: MessageEvent<{ type?: string }>) => {
      if (event.data?.type === "hello") done(true);
    };
    channel.postMessage({ type: "ping" });
  });
}

interface ScreenArea {
  availLeft: number;
  availTop: number;
  availWidth: number;
  availHeight: number;
}
