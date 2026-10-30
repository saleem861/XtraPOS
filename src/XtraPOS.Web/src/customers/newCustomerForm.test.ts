import { describe, expect, it } from "vitest";
import {
  alternatePalestinianPrefix,
  canSaveCustomer,
  customerTypeDefault,
  exceptionalPriceSummary,
  nextCustomerField,
  defaultCallingCode,
  destinationE164,
  displayCallingCode,
  normalizeCallingCodes,
  prefixRetryAllowed,
  secondCallingCode,
  splitPastedCallingCode,
  whatsAppSendError,
  whatsAppLocalFromCustomerNumber,
} from "./newCustomerForm";

const countries = [
  { code: "20", name: "مصر" },
  { code: "970", name: "فلسطين" },
  { code: "972", name: "إسرائيل" },
  { code: "966", name: "السعودية" },
];

describe("new customer form", () => {
  it("copies the customer number into WhatsApp without the first zero", () => {
    expect(whatsAppLocalFromCustomerNumber("0592594707")).toBe("592594707");
    expect(destinationE164(defaultCallingCode, "592594707")).toBe("+972592594707");
    expect(defaultCallingCode).toBe("972");
  });

  it("keeps a manual WhatsApp edit instead of the auto-filled value", () => {
    const auto = whatsAppLocalFromCustomerNumber("0592594707");
    const manual = "599111222";
    expect(auto).toBe("592594707");
    expect(destinationE164("972", manual)).toBe("+972599111222");
  });

  it("lists +972 then +970 and displays a plus on every code", () => {
    const ordered = normalizeCallingCodes(countries);
    expect(ordered[0]?.code).toBe(defaultCallingCode);
    expect(ordered[1]?.code).toBe(secondCallingCode);
    expect(ordered.map((country) => displayCallingCode(country.code)).every((code) => code.startsWith("+"))).toBe(true);
    expect(ordered.some((country) => displayCallingCode(country.code) === "972")).toBe(false);
    expect(destinationE164("970", "592594707")).toBe("+970592594707");
  });

  it("enables save only for the verified destination", () => {
    const base = { verifiedDestination: null as string | null, countryCode: "972", whatsAppLocal: "592594707", name: "سليم", classificationId: "class", priceTypeId: "price" };
    expect(canSaveCustomer(base)).toBe(false);
    expect(canSaveCustomer({ ...base, verifiedDestination: "+972592594707" })).toBe(true);
    expect(canSaveCustomer({ ...base, verifiedDestination: "+972592594707", whatsAppLocal: "599111222" })).toBe(false);
    expect(canSaveCustomer({ ...base, verifiedDestination: "+972592594707", name: "اسم جديد" })).toBe(true);
    expect(canSaveCustomer({ ...base, originalDestination: "+972592594707" })).toBe(true);
    expect(canSaveCustomer({ ...base, originalDestination: "+972592594707", whatsAppLocal: "599111222" })).toBe(false);
  });

  it("falls back only between +972 and +970 and not when WhatsApp is unconfigured", () => {
    expect(alternatePalestinianPrefix("972")).toBe("970");
    expect(alternatePalestinianPrefix("970")).toBe("972");
    expect(alternatePalestinianPrefix("966")).toBeNull();
    expect(prefixRetryAllowed("972", "لا يمكن إرسال رمز التحقق لهذا الرقم لأن حساب Twilio الحالي تجريبي ويسمح فقط بأرقام الاختبار الموثقة.", false)).toBe(false);
    expect(prefixRetryAllowed("972", "تعذر إرسال رمز التحقق حاليًا بسبب إعدادات حساب خدمة الرسائل.", false)).toBe(false);
    expect(prefixRetryAllowed("972", "تعذر إرسال رمز التحقق إلى رقم الهاتف.", false)).toBe(true);
    expect(prefixRetryAllowed("972", "تعذر إرسال رمز التحقق إلى رقم الهاتف.", true)).toBe(false);
    expect(prefixRetryAllowed("966", "تعذر إرسال رمز التحقق إلى رقم الهاتف.", false)).toBe(false);
    expect(whatsAppSendError("WHATSAPP_NOT_CONFIGURED")).toBe("خدمة التحقق عبر الرسائل غير مهيأة بعد.");
  });

  it("keeps two countries that share a calling code", () => {
    const ordered = normalizeCallingCodes([
      { code: "1", name: "كندا" },
      { code: "1", name: "الولايات المتحدة" },
      { code: "33", name: "فرنسا" },
    ]);
    expect(ordered.filter((country) => country.code === "1")).toHaveLength(2);
    expect(ordered[0]?.code).toBe("972");
    expect(ordered[1]?.code).toBe("970");
  });

  it("maps customer type to the catalog price code and discount, then allows a later override to stick until the type changes", () => {
    const prices = [
      { priceTypeId: "p0", priceCode: "Price0" },
      { priceTypeId: "major", priceCode: "MajorCustomer" },
      { priceTypeId: "dealer", priceCode: "Dealer" },
    ];
    expect(customerTypeDefault("عادي", prices)).toEqual({ priceTypeId: "p0", discount: "0" });
    expect(customerTypeDefault("مستهلك كبير", prices)).toEqual({ priceTypeId: "major", discount: "26.2626" });
    expect(customerTypeDefault("محل تجاري", prices)).toEqual({ priceTypeId: "dealer", discount: "31.81818" });
    const manual = { priceTypeId: "p0", discount: "5" };
    const reapplied = customerTypeDefault("مستهلك كبير", prices);
    expect(manual.priceTypeId).not.toBe(reapplied?.priceTypeId);
    expect(reapplied?.discount).toBe("26.2626");
  });

  it("skips price type and discount in the enter path and summarizes exceptional prices", () => {
    expect(nextCustomerField("customer-type")).toBe("customer-notes");
    expect(nextCustomerField("customer-phone")).toBe("customer-country");
    expect(["customer-price", "customer-discount"].includes(nextCustomerField("customer-name") ?? "")).toBe(false);
    expect(exceptionalPriceSummary([])).toBe("لا توجد أسعار استثنائية لهذا الزبون");
    expect(exceptionalPriceSummary([{ itemName: "فحم", specialPrice: 13.5, isActive: true }])).toBe("فحم (13.50)");
  });

  it("splits a pasted international number for display", () => {
    expect(splitPastedCallingCode("+970592594707", countries)).toEqual({ countryCode: "970", localNumber: "592594707" });
    expect(splitPastedCallingCode("00972592594707", countries)).toEqual({ countryCode: "972", localNumber: "592594707" });
  });
});
