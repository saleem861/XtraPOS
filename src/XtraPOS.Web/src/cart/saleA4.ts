import { multiplyDecimals, subtractDecimals } from "./decimal";
import type { CartDraft, CartLine } from "./model";
import { cartTotals, originalCartTotal } from "./model";

export interface A4Line {
  number: string;
  name: string;
  quantity: string;
  unitPrice: string;
  discount: string;
  total: string;
}

export interface A4Document {
  title: string;
  businessName: string;
  branchName: string;
  cashierName: string;
  customerNumber: string;
  customerName: string;
  when: string;
  reference: string | null;
  lines: A4Line[];
  itemCount: number;
  discountTotal: string;
  total: string;
  draft: boolean;
}

export function buildA4Document(input: {
  cart: CartDraft;
  businessName: string;
  branchName: string;
  cashierName: string;
  customerNumber: string | null;
  customerName: string | null;
  when?: Date;
}): A4Document | null {
  if (input.cart.lines.length === 0) return null;
  const totals = cartTotals(input.cart);
  const discount = positiveDifference(originalCartTotal(input.cart), totals.total);
  return {
    title: "أمر بيع غير مكتمل",
    businessName: input.businessName,
    branchName: input.branchName,
    cashierName: input.cashierName,
    customerNumber: input.customerNumber || "—",
    customerName: input.customerName || "زبون عادي",
    when: (input.when ?? new Date()).toLocaleString("ar-EG", { timeZone: "Asia/Hebron" }),
    reference: input.cart.heldSaleId ? `مسودة ${input.cart.heldSaleId.slice(0, 8)}` : null,
    lines: input.cart.lines.map(toLine),
    itemCount: totals.lineCount,
    discountTotal: discount,
    total: totals.total,
    draft: true,
  };
}

export function whatsAppShareUrl(phone: string | null, invoiceUrl: string): string {
  const text = `مرحباً، يمكنكم الاطلاع على فاتورتكم من XtraPOS وتحميلها بصيغة PDF من الرابط التالي:\n${invoiceUrl}`;
  const digits = normalizePhone(phone);
  const base = digits ? `https://wa.me/${digits}` : "https://wa.me/";
  return `${base}?text=${encodeURIComponent(text)}`;
}

export function publicHttpsBase(base: string | undefined): string | null {
  if (!base?.startsWith("https://")) return null;
  try {
    const host = new URL(base).hostname.toLowerCase();
    if (host === "localhost" || host === "127.0.0.1" || host.startsWith("192.168.") || host.startsWith("10.") || host.endsWith(".local")) return null;
    return base.replace(/\/$/, "");
  } catch {
    return null;
  }
}

function normalizePhone(phone: string | null): string {
  const digits = (phone ?? "").replace(/\D/g, "");
  if (digits.startsWith("00")) return digits.slice(2);
  if (digits.startsWith("0") && digits.length === 10) return `972${digits.slice(1)}`;
  return digits.length >= 8 ? digits : "";
}

function toLine(line: CartLine): A4Line {
  if (line.lineType === "Fixed") {
    return {
      number: line.itemNumber,
      name: line.itemName,
      quantity: line.quantity,
      unitPrice: line.resolvedUnitPrice,
      discount: positiveDifference(multiplyDecimals(line.originalUnitPrice, line.quantity), line.lineTotal),
      total: line.lineTotal,
    };
  }
  return {
    number: line.referenceItemNumber,
    name: line.referenceItemName,
    quantity: line.quantity,
    unitPrice: line.finalSellingPrice,
    discount: line.variableValueDiscountPercent ? `${line.variableValueDiscountPercent}%` : "0",
    total: line.lineTotal,
  };
}

function positiveDifference(gross: string, net: string): string {
  const difference = subtractDecimals(gross, net);
  return difference.startsWith("-") ? "0" : difference;
}
