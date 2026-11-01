export const defaultCallingCode = "972";
export const secondCallingCode = "970";

export const fallbackCallingCodes: CallingCodeOption[] = [
  { code: defaultCallingCode, name: "إسرائيل" },
  { code: secondCallingCode, name: "فلسطين" },
];

export function displayCallingCode(code: string): string {
  const digits = code.replace(/^\+/, "");
  return `+${digits}`;
}

export function normalizeCallingCodes(countries: CallingCodeOption[]): CallingCodeOption[] {
  const seen = new Set<string>();
  const unique: CallingCodeOption[] = [];
  for (const country of countries) {
    const code = country.code.replace(/^\+/, "");
    const key = `${code}|${country.name}`;
    if (!code || seen.has(key)) continue;
    seen.add(key);
    unique.push({ code, name: country.name });
  }
  for (const fallback of fallbackCallingCodes) {
    if (!unique.some((country) => country.code === fallback.code)) unique.push(fallback);
  }
  return orderCallingCodes(unique);
}

export const destinationSendError = "تعذر إرسال رمز التحقق إلى رقم الهاتف.";

export function prefixRetryAllowed(countryCode: string, errorMessage: string, usedFallback: boolean): boolean {
  if (usedFallback || !alternatePalestinianPrefix(countryCode)) return false;
  return errorMessage === destinationSendError;
}

export function whatsAppSendError(errorMessage: string): string {
  if (errorMessage.includes("WHATSAPP_NOT_CONFIGURED") || errorMessage.includes("غير مهيأة")) return "خدمة التحقق عبر الرسائل غير مهيأة بعد.";
  return errorMessage || "تعذر إرسال رمز التحقق. حاول مرة أخرى.";
}

const regionalCallingCodes = ["962", "966", "971", "20", "961", "963", "964", "965", "968", "974", "973"];

export interface CallingCodeOption {
  code: string;
  name: string;
}

export function whatsAppLocalFromCustomerNumber(customerNumber: string): string {
  const digits = customerNumber.replace(/\D/g, "");
  return digits.startsWith("0") ? digits.slice(1) : digits;
}

export const otpCodeLength = 4;

export const customerTypeOrder = ["عادي", "برونزي", "فضي", "ذهبي", "محل تجاري", "زبون مميز", "مستهلك كبير"] as const;

const customerTypeDefaults: Record<string, { priceCodes: string[]; discount: number }> = {
  "عادي": { priceCodes: ["Price0"], discount: 0 },
  "برونزي": { priceCodes: ["Price3"], discount: 12 },
  "فضي": { priceCodes: ["Price2"], discount: 18 },
  "ذهبي": { priceCodes: ["Price1"], discount: 22 },
  "محل تجاري": { priceCodes: ["Dealer", "DealerPrice"], discount: 31.81818 },
  "زبون مميز": { priceCodes: ["SpecialCustomer", "SpecialCustomerPrice"], discount: 10 },
  "مستهلك كبير": { priceCodes: ["MajorCustomer", "MajorCustomerPrice"], discount: 26.2626 },
};

export const customerEnterOrder = ["customer-number", "customer-name", "customer-phone", "customer-country", "customer-type", "customer-notes", "send-code", "otp-0", "verify-code", "save-customer"] as const;

export function usesDesktopCustomerKeyboard(): boolean {
  if (typeof window.matchMedia !== "function") return false;
  return window.matchMedia("(pointer: fine) and (hover: hover)").matches;
}

export function customerTypeDefault(name: string, priceTypes: { priceTypeId: string; priceCode: string }[]): { priceTypeId: string; discount: string } | null {
  const spec = customerTypeDefaults[name];
  if (!spec) return null;
  const match = priceTypes.find((price) => spec.priceCodes.some((code) => price.priceCode.localeCompare(code, undefined, { sensitivity: "accent" }) === 0));
  if (!match) return null;
  return { priceTypeId: match.priceTypeId, discount: String(spec.discount) };
}

export function nextCustomerField(current: string): string | null {
  const index = customerEnterOrder.indexOf(current as typeof customerEnterOrder[number]);
  if (index < 0) return customerEnterOrder[0];
  return customerEnterOrder[index + 1] ?? null;
}

export function exceptionalPriceSummary(rows: { itemName: string; specialPrice: number; isActive: boolean }[]): string {
  const active = rows.filter((row) => row.isActive);
  if (active.length === 0) return "لا توجد أسعار استثنائية لهذا الزبون";
  return active.map((row) => `${row.itemName} (${row.specialPrice.toFixed(2)})`).join("، ");
}

export function destinationE164(countryCode: string, localNumber: string): string {
  const local = localNumber.replace(/\D/g, "").replace(/^0+/, "");
  return local ? `+${countryCode}${local}` : "";
}

export function orderCallingCodes(countries: CallingCodeOption[]): CallingCodeOption[] {
  const rank = (code: string) => {
    if (code === defaultCallingCode) return 0;
    if (code === secondCallingCode) return 1;
    const regional = regionalCallingCodes.indexOf(code);
    return regional === -1 ? 100 : 10 + regional;
  };
  return [...countries].sort((left, right) => rank(left.code) - rank(right.code) || left.name.localeCompare(right.name, "ar"));
}

export function alternatePalestinianPrefix(countryCode: string): string | null {
  if (countryCode === "972") return "970";
  if (countryCode === "970") return "972";
  return null;
}

export function splitPastedCallingCode(input: string, countries: CallingCodeOption[]): { countryCode: string; localNumber: string } | null {
  const trimmed = input.trim();
  if (!trimmed.startsWith("+") && !trimmed.startsWith("00")) return null;
  const digits = trimmed.replace(/\D/g, "").replace(/^00/, "");
  const match = [...countries].sort((left, right) => right.code.length - left.code.length).find((country) => digits.startsWith(country.code));
  if (!match) return null;
  return { countryCode: match.code, localNumber: digits.slice(match.code.length).replace(/^0+/, "") };
}

export function canSaveCustomer(input: {
  verifiedDestination: string | null;
  originalDestination?: string | null;
  countryCode: string;
  whatsAppLocal: string;
  name: string;
  classificationId: string;
  priceTypeId: string;
  passkeyVerified?: boolean;
}): boolean {
  const destination = destinationE164(input.countryCode, input.whatsAppLocal);
  const unchanged = input.originalDestination != null && input.originalDestination === destination;
  const verified = Boolean(input.verifiedDestination) && input.verifiedDestination === destination;
  return Boolean(
    (unchanged || verified || input.passkeyVerified)
    && input.name?.trim()
    && input.classificationId
    && input.priceTypeId,
  );
}

export function customerSaveConfirmed(
  sent: { name: string; notes?: string; variableValueDiscountPercent?: number; priceTypeId?: string },
  saved: { name?: string | null; notes?: string | null; variableValueDiscountPercent?: number | string | null; priceTypeId?: string | null; priceCode?: string | null },
  selectedPriceCode?: string | null,
): boolean {
  if ((saved.name ?? "").trim() !== sent.name.trim()) return false;
  if (saved.notes != null && saved.notes.trim() !== (sent.notes ?? "").trim()) return false;
  if (saved.notes === null && (sent.notes ?? "").trim() !== "") return false;
  if (saved.variableValueDiscountPercent != null && saved.variableValueDiscountPercent !== "") {
    const discount = Number(saved.variableValueDiscountPercent);
    if (!Number.isFinite(discount) || Math.abs(discount - (sent.variableValueDiscountPercent ?? 0)) > 0.0001) return false;
  }
  if (saved.priceTypeId && sent.priceTypeId && saved.priceTypeId !== sent.priceTypeId) {
    const sameCode = Boolean(selectedPriceCode) && saved.priceCode != null && saved.priceCode.localeCompare(selectedPriceCode ?? "", undefined, { sensitivity: "accent" }) === 0;
    if (!sameCode) return false;
  }
  if (selectedPriceCode && saved.priceCode && saved.priceCode.localeCompare(selectedPriceCode, undefined, { sensitivity: "accent" }) !== 0) return false;
  return true;
}
