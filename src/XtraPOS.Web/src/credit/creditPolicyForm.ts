export type CreditAccountStatus = "Active" | "Inactive" | "Suspended";

export function creditSwitchState(on: boolean, status: string): { enabled: boolean; status: CreditAccountStatus } {
  if (on) return { enabled: true, status: "Active" };
  return { enabled: false, status: status === "Suspended" ? "Suspended" : "Inactive" };
}

export function creditStatusState(status: string): { enabled: boolean; status: CreditAccountStatus } | null {
  if (status === "Active" || status === "Inactive" || status === "Suspended") {
    return creditSwitchState(status === "Active", status);
  }
  return null;
}

export function creditActivationError(input: { enabled: boolean; limit: string; paymentTermDays: string; status: string }): string | null {
  const terms = input.paymentTermDays.trim();
  if (terms !== "") {
    if (!/^\d+$/.test(terms) || Number(terms) > 3650) return "مدة السداد يجب أن تكون عدداً صحيحاً من 0 إلى 3650 يوماً.";
  } else if (input.enabled) {
    return "أدخل مدة السداد الافتراضية.";
  }

  if (!input.enabled) return null;
  const limit = input.limit.trim();
  if (limit !== "" && !/^\d+(\.\d{1,4})?$/.test(limit)) {
    return "الحد الأقصى يقبل أرقاماً حتى أربع خانات عشرية.";
  }
  if (input.status !== "Active") return "تفعيل الاستدانة يضبط الحساب إلى مفعّل.";
  return null;
}
