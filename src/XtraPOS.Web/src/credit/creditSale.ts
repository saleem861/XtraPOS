export interface CreditQuoteInput {
  invoiceTotal: number;
  immediatePayment: number;
}

export function deferredAmount(input: CreditQuoteInput): { deferred: number } | { error: string } {
  if (!Number.isFinite(input.invoiceTotal) || input.invoiceTotal <= 0) return { error: "إجمالي الفاتورة غير صالح." };
  if (!Number.isFinite(input.immediatePayment) || input.immediatePayment < 0) return { error: "المدفوع الآن غير صالح." };
  const deferred = Math.round((input.invoiceTotal - input.immediatePayment) * 10000) / 10000;
  if (deferred < 0) return { error: "لا يمكن أن يتجاوز المدفوع الآن إجمالي الفاتورة." };
  if (deferred === 0) return { error: "المبلغ المؤجل صفر. استخدم إتمام البيع العادي." };
  return { deferred };
}

export function creditEligible(policy: { creditEnabled: boolean; creditStatus: string; available: number; centralReachable: boolean } | null, customerId: string | null, canCreate: boolean): string | null {
  if (!canCreate) return "لا توجد صلاحية للبيع بالدين.";
  if (!customerId) return "اختر زبوناً مسجلاً. زبون عادي لا يُمنح ديناً.";
  if (!policy || !policy.centralReachable) return "جهة الائتمان المركزية غير متاحة.";
  if (!policy.creditEnabled || policy.creditStatus !== "Active") return "حساب الائتمان غير نشط.";
  if (policy.available <= 0) return "لا يوجد رصيد ائتمان متاح.";
  return null;
}
