import { act, cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { DeviceSession } from "../types/setup";
import { NewCustomerScreen } from "./NewCustomerScreen";

const session = {
  deviceId: "device",
  businessId: "business",
  branchId: "branch",
  stationId: "station",
  deviceName: "صندوق",
  deviceType: "POS",
  deviceToken: "token",
  registeredAt: "2026-10-22",
  businessName: "Smoker World",
  branchName: "فرع",
  branchType: "Retail",
  stationName: "كاونتر",
  stationNumber: 1,
} as DeviceSession;

function json(body: unknown, ok = true) {
  return Promise.resolve({ ok, status: ok ? 200 : 400, json: async () => body } as Response);
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe("إضافة زبون جديد", () => {
  it("keeps save disabled until verification and selects the customer after save", async () => {
    const saved: string[] = [];
    vi.stubGlobal("fetch", vi.fn((url: string, init?: RequestInit) => {
      if (String(url).endsWith("/classifications")) return json({ classifications: [{ customerClassificationId: "class", name: "عادي" }] });
      if (String(url).endsWith("/price-types")) return json([{ priceTypeId: "price", priceCode: "Price0", priceName: "السعر" }]);
      if (String(url).endsWith("/calling-codes")) return json([{ code: "972", name: "إسرائيل" }, { code: "970", name: "فلسطين" }, { code: "966", name: "السعودية" }]);
      if (String(url).endsWith("/phone-verification/send")) return json({ verificationId: "verify", expiresAt: "", resendAvailableAt: new Date(Date.now() + 60000).toISOString(), maskedDestination: "+97259*****07" });
      if (String(url).endsWith("/phone-verification/verify")) return json({ status: "Verified" });
      if (String(url).includes("/api/customers/") && (!init || init.method === "GET")) return json({ customerId: "customer-1", name: "سليم", customerNumber: "0592594707", priceCode: "Price0", classificationName: "عادي" });
      return json({ customer: { customerId: "customer-1", name: "سليم", customerNumber: "0592594707", rowVersion: "AQIDBAUGBwg=", phoneVerificationStatus: "Verified", whatsAppPhoneE164: "+972592594707", priceCode: "Price0", classificationName: "عادي" } });
    }));
    render(<NewCustomerScreen session={session} onBack={() => undefined} onSaved={(id) => saved.push(id)} />);
    expect((await screen.findByRole("button", { name: "حفظ الزبون" }) as HTMLButtonElement).disabled).toBe(true);
    expect(screen.queryByRole("button", { name: "توليد Passkey" })).toBeTruthy();
    fireEvent.change(screen.getByLabelText("رقم الزبون"), { target: { value: "0592594707" } });
    expect((screen.getByLabelText("رقم الهاتف للتحقق") as HTMLInputElement).value).toBe("592594707");
    fireEvent.change(screen.getByLabelText("اسم الزبون"), { target: { value: "سليم" } });
    expect(screen.queryByText("أوافق على استلام رمز التحقق عبر واتساب")).toBeNull();
    const country = screen.getByRole("combobox", { name: "مقدمة الدولة" });
    fireEvent.click(country);
    const countryOptions = screen.getAllByRole("option");
    expect(countryOptions[0]?.textContent).toContain("+972");
    expect(countryOptions[1]?.textContent).toContain("+970");
    expect(countryOptions.every((option) => option.textContent?.includes("+"))).toBe(true);
    fireEvent.click(countryOptions[0]);
    fireEvent.click(await screen.findByLabelText("نوع الزبون"));
    fireEvent.click(screen.getByRole("option", { name: "عادي" }));
    fireEvent.click(screen.getByLabelText("نوع التسعيرة (كود النظام)"));
    fireEvent.click(screen.getByRole("option", { name: "Price0" }));
    fireEvent.click(screen.getByRole("button", { name: "إرسال رمز التحقق" }));
    const calls = (globalThis.fetch as ReturnType<typeof vi.fn>).mock.calls.map((call) => String(call[0]));
    expect(calls.some((url) => url === "/api/customers" || url.endsWith("/api/customers"))).toBe(false);
    expect(calls).toContain("/api/customers/phone-verification/send");
    const first = await screen.findByLabelText("رمز 1");
    fireEvent.change(first, { target: { value: "12" } });
    expect((screen.getByRole("button", { name: "التحقق من الرمز" }) as HTMLButtonElement).disabled).toBe(true);
    fireEvent.change(first, { target: { value: "1234" } });
    expect(screen.queryByLabelText("رمز 5")).toBeNull();
    expect(screen.queryByLabelText("رمز 6")).toBeNull();
    expect((screen.getByRole("button", { name: "حفظ الزبون" }) as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(screen.getByRole("button", { name: "التحقق من الرمز" }));
    expect((await screen.findAllByText("تم التحقق من رقم الهاتف بنجاح")).length).toBeGreaterThan(0);
    expect((screen.getByRole("button", { name: "حفظ الزبون" }) as HTMLButtonElement).disabled).toBe(false);
    fireEvent.change(screen.getByLabelText("اسم الزبون"), { target: { value: "سليم أحمد" } });
    expect((screen.getByRole("button", { name: "حفظ الزبون" }) as HTMLButtonElement).disabled).toBe(false);
    fireEvent.click(screen.getByLabelText("مقدمة الدولة"));
    fireEvent.click(screen.getByRole("option", { name: /فلسطين/ }));
    expect((screen.getByRole("button", { name: "حفظ الزبون" }) as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(screen.getByLabelText("مقدمة الدولة"));
    fireEvent.click(screen.getByRole("option", { name: /إسرائيل/ }));
    fireEvent.change(screen.getByLabelText("رقم الهاتف للتحقق"), { target: { value: "599111222" } });
    expect((screen.getByRole("button", { name: "حفظ الزبون" }) as HTMLButtonElement).disabled).toBe(true);
    fireEvent.change(screen.getByLabelText("رقم الهاتف للتحقق"), { target: { value: "592594707" } });
    fireEvent.click(screen.getByRole("button", { name: "التحقق من الرمز" }));
    expect((await screen.findByRole("button", { name: "حفظ الزبون" }) as HTMLButtonElement).disabled).toBe(false);
    await act(async () => { fireEvent.click(screen.getByRole("button", { name: "حفظ الزبون" })); });
    expect(saved).toEqual(["customer-1"]);
    const saveCall = (globalThis.fetch as ReturnType<typeof vi.fn>).mock.calls.find((call) => String(call[0]).endsWith("/api/customers") && call[1]?.method === "POST");
    const payload = JSON.parse(String(saveCall?.[1].body));
    expect(payload).toMatchObject({
      customerNumber: "0592594707",
      name: "سليم أحمد",
      customerClassificationId: "class",
      priceTypeId: "price",
      verificationId: "verify",
      phone: "592594707",
      whatsAppCountryCode: "972",
      notes: "",
    });
    expect(payload.otp).toBeUndefined();
  });

  it("keeps notes on the left slot, shows four otp boxes, and opens the exceptional-price screen", async () => {
    vi.stubGlobal("fetch", vi.fn((url: string) => {
      if (String(url).endsWith("/classifications")) return json({ classifications: [{ customerClassificationId: "class", name: "عادي" }] });
      if (String(url).endsWith("/price-types")) return json([{ priceTypeId: "price", priceCode: "Price0", priceName: "السعر" }]);
      if (String(url).endsWith("/calling-codes")) return json([{ code: "972", name: "إسرائيل" }]);
      if (String(url).includes("special-prices")) return json([]);
      return json({});
    }));
    vi.stubGlobal("matchMedia", vi.fn(() => ({ matches: false, addEventListener: () => undefined, removeEventListener: () => undefined })));
    render(<NewCustomerScreen session={session} customerId="customer-1" onBack={() => undefined} onSaved={() => undefined} />);
    const notes = await screen.findByLabelText("ملاحظات");
    expect(notes.closest(".is-second")?.lastElementChild?.contains(notes)).toBe(true);
    expect(screen.getAllByLabelText(/رمز /)).toHaveLength(4);
    expect(screen.getByRole("button", { name: "توليد Passkey" })).toBeTruthy();
    expect(screen.queryByLabelText("لوحة المفاتيح")).toBeNull();
    expect(screen.getByText("لا توجد أسعار استثنائية لهذا الزبون")).toBeTruthy();
    expect(screen.queryByRole("table")).toBeNull();
    fireEvent.keyDown(screen.getByLabelText("رقم الزبون"), { key: "Enter" });
    expect(document.activeElement).toBe(screen.getByLabelText("اسم الزبون"));
    fireEvent.keyDown(screen.getByLabelText("نوع الزبون"), { key: "Enter" });
    expect(document.activeElement).toBe(notes);
    expect(document.activeElement).not.toBe(screen.getByLabelText("نوع التسعيرة (كود النظام)"));
    expect(document.activeElement).not.toBe(screen.getByLabelText("نسبة الخصم"));
    fireEvent.click(screen.getByRole("button", { name: "إضافة / تعديل الأصناف الاستثنائية" }));
    expect(screen.getByRole("region", { name: "الأسعار الاستثنائية للزبون" })).toBeTruthy();
    expect(screen.getByText("الأصناف الاستثنائية الحالية")).toBeTruthy();
    expect(screen.queryByLabelText("رقم الزبون")).toBeNull();
  });

  it("renders the custom keyboard only for a fine pointer with hover", async () => {
    vi.stubGlobal("fetch", vi.fn((url: string) => {
      if (String(url).endsWith("/classifications")) return json({ classifications: [] });
      if (String(url).endsWith("/price-types")) return json([]);
      if (String(url).endsWith("/calling-codes")) return json([]);
      return json({});
    }));
    vi.stubGlobal("matchMedia", vi.fn((query: string) => ({ matches: query.includes("pointer: fine"), addEventListener: () => undefined, removeEventListener: () => undefined })));
    render(<NewCustomerScreen session={session} onBack={() => undefined} onSaved={() => undefined} />);
    expect(await screen.findByLabelText("لوحة المفاتيح")).toBeTruthy();
  });

  it("saves an existing customer without a new verification code", async () => {
    const saved: string[] = [];
    vi.stubGlobal("fetch", vi.fn((url: string, init?: RequestInit) => {
      if (String(url).endsWith("/classifications")) return json({ classifications: [{ customerClassificationId: "class", name: "عادي" }] });
      if (String(url).endsWith("/price-types")) return json([{ priceTypeId: "price", priceCode: "Price0", priceName: "السعر" }]);
      if (String(url).endsWith("/calling-codes")) return json([{ code: "972", name: "إسرائيل" }]);
      if (String(url).includes("special-prices")) return json([]);
      if (String(url).includes("credit-policy")) return json({ creditEnabled: false, creditLimit: 0, paymentTermDays: 30, creditStatus: "Inactive", rowVersion: "", configured: false, outstanding: 0, available: 0, centralReachable: false });
      if (init?.method === "PUT") return json({ customer: { customerId: "customer-1", name: "سليم أحمد", customerNumber: "0592594707", rowVersion: "AQIDBAUGBwg=", phoneVerificationStatus: "Verified", whatsAppPhoneE164: "+972592594707", priceCode: "Price0", classificationName: "عادي" } });
      return json({ customerId: "customer-1", name: "سليم", customerNumber: "0592594707", customerClassificationId: "class", priceTypeId: "price", rowVersion: "AQIDBAUGBwg=", phoneVerificationStatus: "Verified", whatsAppPhoneE164: "+972592594707" });
    }));
    render(<NewCustomerScreen session={session} customerId="customer-1" title="تعديل زبون" onBack={() => undefined} onSaved={(id) => saved.push(id)} />);
    const save = await screen.findByRole("button", { name: "حفظ" });
    expect((save as HTMLButtonElement).disabled).toBe(false);
    fireEvent.change(screen.getByLabelText("اسم الزبون"), { target: { value: "سليم أحمد" } });
    await act(async () => { fireEvent.click(save); });
    expect(saved).toEqual(["customer-1"]);
    expect(await screen.findByRole("dialog", { name: "تم حفظ تعديلات الزبون بنجاح" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "موافق" })).toBeTruthy();
    const update = (globalThis.fetch as ReturnType<typeof vi.fn>).mock.calls.find((call) => call[1]?.method === "PUT");
    expect(String(update?.[0])).toBe("/api/customers/customer-1");
    expect(JSON.parse(String(update?.[1].body))).toMatchObject({ name: "سليم أحمد", customerClassificationId: "class", priceTypeId: "price" });
  });

  it("sends the central price type when the opened customer still has the branch id", async () => {
    vi.stubGlobal("fetch", vi.fn((url: string, init?: RequestInit) => {
      if (String(url).endsWith("/classifications")) return json({ classifications: [{ customerClassificationId: "class", name: "زبون مميز" }] });
      if (String(url).endsWith("/price-types")) return json([{ priceTypeId: "central-price", priceCode: "SpecialCustomer", priceName: "مميز" }]);
      if (String(url).endsWith("/calling-codes")) return json([{ code: "972", name: "إسرائيل" }]);
      if (String(url).includes("special-prices")) return json([]);
      if (String(url).includes("credit-policy")) return json({ creditEnabled: false, creditLimit: 0, paymentTermDays: 30, creditStatus: "Inactive", rowVersion: "", configured: false, outstanding: 0, available: 0, centralReachable: true });
      if (init?.method === "PUT") {
        return json({ customer: { customerId: "customer-1", name: "أسيد حمدان", customerNumber: "0595497078", customerClassificationId: "class", priceTypeId: "central-price", priceCode: "SpecialCustomer", notes: "ملاحظة محفوظة", variableValueDiscountPercent: 10, rowVersion: "AQIDBAUGBwg=", phoneVerificationStatus: "Verified", whatsAppPhoneE164: "+972595497078" } });
      }
      return json({ customerId: "customer-1", name: "أسيد حمدان", customerNumber: "0595497078", customerClassificationId: "class", priceTypeId: "branch-price", priceCode: "SpecialCustomer", variableValueDiscountPercent: 10, notes: "", rowVersion: "AQIDBAUGBwg=", phoneVerificationStatus: "Verified", whatsAppPhoneE164: "+972595497078" });
    }));
    render(<NewCustomerScreen session={session} customerId="customer-1" permissions={["Customers.Manage"]} onBack={() => undefined} onSaved={() => undefined} />);
    expect((await screen.findByRole("combobox", { name: "نوع التسعيرة (كود النظام)" })).textContent).toContain("SpecialCustomer");
    fireEvent.change(screen.getByLabelText("ملاحظات"), { target: { value: "ملاحظة محفوظة" } });
    await act(async () => { fireEvent.click(screen.getByRole("button", { name: "حفظ" })); });
    expect(await screen.findByRole("dialog", { name: "تم حفظ تعديلات الزبون بنجاح" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "موافق" })).toBeTruthy();
    const update = (globalThis.fetch as ReturnType<typeof vi.fn>).mock.calls.find((call) => call[1]?.method === "PUT");
    expect(JSON.parse(String(update?.[1].body))).toMatchObject({ priceTypeId: "central-price", notes: "ملاحظة محفوظة", variableValueDiscountPercent: 10 });
  });

  it("shows the stored discount and fills a missing price type from the customer type", async () => {
    vi.stubGlobal("fetch", vi.fn((url: string) => {
      if (String(url).endsWith("/classifications")) return json({ classifications: [{ customerClassificationId: "class", name: "محل تجاري" }] });
      if (String(url).endsWith("/price-types")) return json([{ priceTypeId: "dealer", priceCode: "Dealer", priceName: "تاجر" }]);
      if (String(url).endsWith("/calling-codes")) return json([{ code: "972", name: "إسرائيل" }]);
      if (String(url).includes("special-prices")) return json([]);
      if (String(url).includes("credit-policy")) return json({ creditEnabled: false, creditLimit: 0, paymentTermDays: 30, creditStatus: "Inactive", rowVersion: "", configured: false, outstanding: 0, available: 0, centralReachable: true });
      return json({
        customerId: "customer-1",
        name: "محل النور",
        customerNumber: "100",
        customerClassificationId: "class",
        classificationName: "محل تجاري",
        priceTypeId: null,
        priceCode: null,
        variableValueDiscountPercent: 27.27,
        rowVersion: "AQIDBAUGBwg=",
        phoneVerificationStatus: "Verified",
        whatsAppPhoneE164: "+972590000002",
      });
    }));
    render(<NewCustomerScreen session={session} customerId="customer-1" onBack={() => undefined} onSaved={() => undefined} />);
    expect(await screen.findByDisplayValue("27.27")).toBeTruthy();
    expect((await screen.findByRole("combobox", { name: "نوع التسعيرة (كود النظام)" })).textContent).toContain("Dealer");
    expect((screen.getByLabelText("نسبة الخصم") as HTMLInputElement).value).toBe("27.27");
    expect(await screen.findByText("الرصيد القائم 0 — المتاح 0")).toBeTruthy();
    expect(screen.queryByRole("button", { name: "إعادة المحاولة" })).toBeNull();
  });

  it("ties the debt switch to an active account and a positive limit", async () => {
    vi.stubGlobal("fetch", vi.fn((url: string) => {
      if (String(url).endsWith("/classifications")) return json({ classifications: [] });
      if (String(url).endsWith("/price-types")) return json([]);
      if (String(url).endsWith("/calling-codes")) return json([]);
      return json({});
    }));
    render(<NewCustomerScreen session={session} permissions={["System.Administer"]} onBack={() => undefined} onSaved={() => undefined} />);
    const allowDebt = await screen.findByRole("switch", { name: "السماح بالبيع بالدين" });
    expect((allowDebt as HTMLInputElement).checked).toBe(false);
    expect(screen.getByRole("combobox", { name: "حالة الحساب الائتماني" }).textContent).toContain("غير مفعّل");
    fireEvent.click(allowDebt);
    expect((allowDebt as HTMLInputElement).checked).toBe(true);
    expect(screen.getByRole("combobox", { name: "حالة الحساب الائتماني" }).textContent).toContain("مفعّل");
    fireEvent.click(screen.getByRole("combobox", { name: "حالة الحساب الائتماني" }));
    fireEvent.click(screen.getByRole("option", { name: "موقوف" }));
    expect((screen.getByRole("switch", { name: "السماح بالبيع بالدين" }) as HTMLInputElement).checked).toBe(false);
    expect(screen.getByRole("combobox", { name: "حالة الحساب الائتماني" }).textContent).toContain("موقوف");
  });

  it("keeps debt activation available when an existing customer policy cannot be loaded", async () => {
    vi.stubGlobal("fetch", vi.fn((url: string) => {
      if (String(url).endsWith("/classifications")) return json({ classifications: [] });
      if (String(url).endsWith("/price-types")) return json([]);
      if (String(url).endsWith("/calling-codes")) return json([]);
      if (String(url).includes("credit-policy")) return json({ message: "تعذر الاتصال بجهة الائتمان المركزية." }, false);
      return json({ customerId: "customer-1", name: "سليم", customerNumber: "1", rowVersion: "AQIDBAUGBwg=", phoneVerificationStatus: "Verified", whatsAppPhoneE164: "+972592594707" });
    }));
    render(<NewCustomerScreen session={session} customerId="customer-1" permissions={["Credit.ManageLimit"]} readOnly title="عرض زبون" onBack={() => undefined} onSaved={() => undefined} />);
    expect(await screen.findByRole("button", { name: "إعادة المحاولة" })).toBeTruthy();
    expect(screen.getByText("الرصيد القائم والمتاح غير معروفين.")).toBeTruthy();
    const allowDebt = await screen.findByRole("switch", { name: "السماح بالبيع بالدين" });
    expect((allowDebt as HTMLInputElement).disabled).toBe(false);
    fireEvent.click(allowDebt);
    expect(screen.getByRole("combobox", { name: "حالة الحساب الائتماني" }).textContent).toContain("مفعّل");
    expect(screen.getByRole("button", { name: "حفظ الاستدانة" })).toBeTruthy();
  });

  it("hides save when an existing customer is opened without edit permission", async () => {
    vi.stubGlobal("fetch", vi.fn((url: string) => {
      if (String(url).endsWith("/classifications")) return json({ classifications: [] });
      if (String(url).endsWith("/price-types")) return json([]);
      if (String(url).endsWith("/calling-codes")) return json([]);
      return json({ customerId: "customer-1", name: "سليم", customerNumber: "1", rowVersion: "AQIDBAUGBwg=" });
    }));
    render(<NewCustomerScreen session={session} customerId="customer-1" readOnly onBack={() => undefined} onSaved={() => undefined} />);
    expect(await screen.findByLabelText("اسم الزبون")).toBeTruthy();
    expect(screen.queryByRole("button", { name: "حفظ الزبون" })).toBeNull();
    expect(screen.queryByRole("button", { name: "إرسال رمز التحقق" })).toBeNull();
  });
});
