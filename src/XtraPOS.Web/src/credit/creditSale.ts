export interface CreditQuoteInput {
  invoiceTotal: number;
  immediatePayment: number;
}

export function deferredAmount(input: CreditQuoteInput): { deferred: number } | { error: string } {
  const split = splitDeferred(input.invoiceTotal, Math.round((input.invoiceTotal - input.immediatePayment) * 10000) / 10000);
  if ("error" in split) return split;
  if (!Number.isFinite(input.immediatePayment) || input.immediatePayment < 0) return { error: "المدفوع الآن غير صالح." };
  return { deferred: split.deferred };
}

export function splitDeferred(invoiceTotal: number, deferredInput: number): { deferred: number; immediate: number } | { error: string } {
  if (!Number.isFinite(invoiceTotal) || invoiceTotal <= 0) return { error: "إجمالي الفاتورة غير صالح." };
  if (!Number.isFinite(deferredInput) || deferredInput < 0) return { error: "القيمة المؤجلة غير صالحة." };
  const deferred = Math.round(deferredInput * 10000) / 10000;
  const immediate = Math.round((invoiceTotal - deferred) * 10000) / 10000;
  if (immediate < 0) return { error: "لا يمكن أن تتجاوز القيمة المؤجلة إجمالي الفاتورة." };
  if (deferred === 0) return { error: "المبلغ المؤجل صفر. استخدم إتمام البيع العادي." };
  return { deferred, immediate };
}

export function appendDeferred(current: string, digit: string): string {
  const cleaned = current.replace(/[^\d.]/g, "");
  if (digit === ".") return cleaned.includes(".") ? cleaned : `${cleaned || "0"}.`;
  if (digit === "00") return `${cleaned || "0"}00`.replace(/^0+(?=\d)/, "");
  const next = `${cleaned}${digit}`.replace(/^0+(?=\d)/, "");
  const [whole, fraction] = next.split(".");
  if (fraction && fraction.length > 2) return cleaned;
  return fraction === undefined ? whole : `${whole}.${fraction}`;
}

export function creditEligible(policy: { creditEnabled: boolean; creditStatus: string; available: number; centralReachable: boolean } | null, customerId: string | null, canCreate: boolean): string | null {
  if (!canCreate) return "لا توجد صلاحية للبيع بالدين.";
  if (!customerId) return "اختر زبوناً مسجلاً. زبون عادي لا يُمنح ديناً.";
  if (!policy || !policy.centralReachable) return "جهة الائتمان المركزية غير متاحة.";
  if (!policy.creditEnabled || policy.creditStatus !== "Active") return "حساب الائتمان غير نشط.";
  if (policy.available <= 0) return "لا يوجد رصيد ائتمان متاح.";
  return null;
}
