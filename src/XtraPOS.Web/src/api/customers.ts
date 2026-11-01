import { deviceHeaders, SetupClientError } from "./client";
import type { DeviceSession } from "../types/setup";

export interface SpecialPriceRow {
  customerItemSpecialPriceId: string;
  itemId: string;
  itemNumber: string;
  itemName: string;
  basePrice: number;
  specialPrice: number;
  minimumQuantity: number;
  bundlePrice: number;
  validFrom: string | null;
  validTo: string | null;
  isActive: boolean;
  rowVersion: string;
}

export interface CreditPolicy {
  customerId: string;
  creditEnabled: boolean;
  creditLimit: number;
  paymentTermDays: number;
  creditStatus: string;
  rowVersion: string;
  configured: boolean;
  outstanding: number;
  available: number;
  centralReachable: boolean;
}

export interface SavedCustomer {
  customerId: string;
  customerNumber: string | null;
  name: string;
  rowVersion: string;
  phoneVerificationStatus: string | null;
  whatsAppPhoneE164: string | null;
  priceCode: string | null;
  priceTypeId: string | null;
  classificationName: string | null;
  customerClassificationId: string | null;
  notes: string | null;
  variableValueDiscountPercent: number | null;
}

export interface CustomerWriteBody {
  customerNumber: string;
  name: string;
  phone: string;
  whatsAppCountryCode: string;
  whatsAppConsent: boolean;
  customerClassificationId: string;
  priceTypeId: string;
  notes?: string;
  variableValueDiscountPercent?: number;
  rowVersion?: string;
  verificationId?: string;
  passkeySessionId?: string;
  specialPrices?: { itemNumber: string; minimumQuantity: number; bundlePrice: number }[];
}

export interface CustomerListItem {
  customerId: string;
  customerNumber: string | null;
  name: string;
  phone: string | null;
  classificationName: string | null;
  priceCode: string | null;
  variableValueDiscountPercent: string;
  customerClassificationId?: string | null;
  priceTypeId?: string | null;
  notes?: string | null;
  rowVersion?: string | null;
  whatsAppPhoneE164?: string | null;
  phoneVerificationStatus?: string | null;
}

interface CustomerPayload {
  customerId: string;
  customerNumber?: string | null;
  name: string;
  phone?: string | null;
  classificationName?: string | null;
  priceCode?: string | null;
  customerClassificationId?: string | null;
  priceTypeId?: string | null;
  notes?: string | null;
  variableValueDiscountPercent?: number | string | null;
  rowVersion?: string;
  phoneVerificationStatus?: string | null;
  whatsAppPhoneE164?: string | null;
}

function mapCustomer(customer: CustomerPayload): CustomerListItem {
  return {
    customerId: customer.customerId,
    customerNumber: customer.customerNumber ?? null,
    name: customer.name,
    phone: customer.phone ?? null,
    classificationName: customer.classificationName ?? null,
    priceCode: customer.priceCode ?? null,
    variableValueDiscountPercent: customer.variableValueDiscountPercent == null || customer.variableValueDiscountPercent === "" ? "" : String(customer.variableValueDiscountPercent),
    customerClassificationId: customer.customerClassificationId ?? null,
    priceTypeId: customer.priceTypeId ?? null,
    notes: customer.notes ?? null,
    rowVersion: customer.rowVersion ?? null,
    whatsAppPhoneE164: customer.whatsAppPhoneE164 ?? null,
    phoneVerificationStatus: customer.phoneVerificationStatus ?? null,
  };
}

interface CustomerPage {
  totalCount: number;
  customers: CustomerPayload[];
}

export function createCustomerClient(session: DeviceSession) {
  return {
    async search(search: string, page = 1): Promise<{ customers: CustomerListItem[]; totalCount: number; syncState: string | null }> {
      const params = new URLSearchParams({ search, active: "active", page: String(page), pageSize: "25" });
      const response = await fetch(`/api/customers?${params}`, { headers: deviceHeaders(session) });
      if (response.status === 401) throw new SetupClientError("انتهت الجلسة. ارجع وسجّل الدخول مرة أخرى.");
      if (!response.ok) throw new SetupClientError("تعذر تحميل بيانات الزبائن");
      const body = await response.json() as CustomerPage & { syncState?: string | null };
      return { customers: (body.customers ?? []).map(mapCustomer), totalCount: body.totalCount ?? 0, syncState: body.syncState ?? null };
    },
    async classifications(): Promise<{ customerClassificationId: string; name: string }[]> {
      const response = await fetch("/api/customers/classifications", { headers: deviceHeaders(session) });
      if (!response.ok) throw new SetupClientError("تعذر تحميل أنواع الزبائن.");
      const body = await response.json() as { classifications?: { customerClassificationId: string; name: string }[] };
      return body.classifications ?? [];
    },
    async priceTypes(): Promise<{ priceTypeId: string; priceCode: string; priceName: string }[]> {
      const response = await fetch("/api/customers/price-types", { headers: deviceHeaders(session) });
      if (!response.ok) throw new SetupClientError("تعذر تحميل التسعيرات.");
      return await response.json() as { priceTypeId: string; priceCode: string; priceName: string }[];
    },
    async callingCodes(): Promise<{ code: string; name: string }[]> {
      const response = await fetch("/api/customers/calling-codes", { headers: deviceHeaders(session) });
      if (!response.ok) throw new SetupClientError("تعذر تحميل مقدمات الدول.");
      const body = await response.json() as { code?: string; Code?: string; name?: string; Name?: string }[];
      return body.map((country) => ({ code: country.code ?? country.Code ?? "", name: country.name ?? country.Name ?? "" }));
    },
    async saveCustomer(body: CustomerWriteBody, customerId?: string): Promise<SavedCustomer> {
      const response = await fetch(customerId ? `/api/customers/${customerId}` : "/api/customers", {
        method: customerId ? "PUT" : "POST",
        headers: { ...deviceHeaders(session), "Content-Type": "application/json" },
        body: JSON.stringify({ ...body, isActive: true }),
      });
      const payload = await response.json() as { message?: string; customer?: CustomerPayload };
      if (!response.ok || !payload.customer) throw new SetupClientError(payload.message ?? "تعذر حفظ الزبون.");
      const discount = payload.customer.variableValueDiscountPercent;
      return {
        customerId: payload.customer.customerId,
        customerNumber: payload.customer.customerNumber ?? null,
        name: payload.customer.name,
        rowVersion: payload.customer.rowVersion ?? "",
        phoneVerificationStatus: payload.customer.phoneVerificationStatus ?? null,
        whatsAppPhoneE164: payload.customer.whatsAppPhoneE164 ?? null,
        priceCode: payload.customer.priceCode ?? null,
        priceTypeId: payload.customer.priceTypeId ?? null,
        classificationName: payload.customer.classificationName ?? null,
        customerClassificationId: payload.customer.customerClassificationId ?? null,
        notes: payload.customer.notes ?? null,
        variableValueDiscountPercent: discount == null || discount === "" ? null : Number(discount),
      };
    },
    async sendRegistration(customerNumber: string, destinationE164: string): Promise<{ verificationId: string; expiresAt: string; resendAvailableAt: string; maskedDestination: string }> {
      const response = await fetch("/api/customers/phone-verification/send", {
        method: "POST",
        headers: { ...deviceHeaders(session), "Content-Type": "application/json" },
        body: JSON.stringify({ customerNumber, destinationE164 }),
      });
      const payload = await response.json() as { message?: string; verificationId: string; expiresAt: string; resendAvailableAt: string; maskedDestination: string };
      if (!response.ok) throw new SetupClientError(payload.message ?? "تعذر إرسال رمز التحقق.");
      return payload;
    },
    async verifyRegistration(verificationId: string, destinationE164: string, otp: string): Promise<void> {
      const response = await fetch("/api/customers/phone-verification/verify", {
        method: "POST",
        headers: { ...deviceHeaders(session), "Content-Type": "application/json" },
        body: JSON.stringify({ verificationId, destinationE164, otp }),
      });
      if (!response.ok) {
        const payload = await response.json() as { message?: string };
        throw new SetupClientError(payload.message ?? "رمز التحقق غير صحيح.");
      }
    },
    async sendVerification(customerId: string): Promise<{ verificationId: string; expiresAt: string; resendAvailableAt: string; maskedDestination: string }> {
      const response = await fetch(`/api/customers/${customerId}/phone-verification/send`, { method: "POST", headers: deviceHeaders(session) });
      const payload = await response.json() as { message?: string; verificationId: string; expiresAt: string; resendAvailableAt: string; maskedDestination: string };
      if (!response.ok) throw new SetupClientError(payload.message ?? "تعذر إرسال رمز واتساب.");
      return payload;
    },
    async verifyPhone(customerId: string, verificationId: string, otp: string): Promise<void> {
      const response = await fetch(`/api/customers/${customerId}/phone-verification/verify`, {
        method: "POST",
        headers: { ...deviceHeaders(session), "Content-Type": "application/json" },
        body: JSON.stringify({ verificationId, otp }),
      });
      if (!response.ok) {
        const payload = await response.json() as { message?: string };
        throw new SetupClientError(payload.message ?? "رمز التحقق غير صحيح.");
      }
    },
    async creditPolicy(customerId: string): Promise<CreditPolicy> {
      const response = await fetch(`/api/customers/${customerId}/credit-policy`, { headers: deviceHeaders(session) });
      const payload = await response.json() as CreditPolicy & { message?: string };
      if (response.status === 403) throw new SetupClientError("لا توجد صلاحية لعرض الائتمان.");
      if (!response.ok) throw new SetupClientError(payload.message ?? "تعذر الاتصال بجهة الائتمان المركزية.");
      return payload;
    },
    async saveCreditPolicy(customerId: string, body: { creditEnabled: boolean; creditLimit: number; paymentTermDays: number; creditStatus: string; rowVersion?: string; reason?: string }): Promise<CreditPolicy> {
      const response = await fetch(`/api/customers/${customerId}/credit-policy`, {
        method: "PUT",
        headers: { ...deviceHeaders(session), "Content-Type": "application/json" },
        body: JSON.stringify(body),
      });
      const payload = await response.json() as CreditPolicy & { message?: string };
      if (!response.ok) throw new SetupClientError(payload.message ?? "تعذر حفظ إعدادات الائتمان.");
      return payload;
    },
    async specialPrices(customerId: string): Promise<SpecialPriceRow[]> {
      const response = await fetch(`/api/customers/${customerId}/special-prices`, { headers: deviceHeaders(session) });
      if (!response.ok) throw new SetupClientError("تعذر تحميل الأسعار الخاصة.");
      const rows = await response.json() as SpecialPriceRow[];
      return rows.map((row) => ({
        ...row,
        minimumQuantity: row.minimumQuantity ?? 1,
        bundlePrice: row.bundlePrice ?? row.specialPrice,
        itemId: row.itemId ?? "",
      }));
    },
    async saveSpecialPrice(customerId: string, body: { itemNumber: string; specialPrice: number; minimumQuantity?: number; bundlePrice?: number; priceId?: string; rowVersion?: string; isActive?: boolean }): Promise<void> {
      const response = await fetch(body.priceId ? `/api/customers/${customerId}/special-prices/${body.priceId}` : `/api/customers/${customerId}/special-prices`, {
        method: body.priceId ? "PUT" : "POST",
        headers: { ...deviceHeaders(session), "Content-Type": "application/json" },
        body: JSON.stringify({
          itemNumber: body.itemNumber,
          specialPrice: body.specialPrice,
          minimumQuantity: body.minimumQuantity ?? 1,
          bundlePrice: body.bundlePrice ?? body.specialPrice,
          isActive: body.isActive ?? true,
          rowVersion: body.rowVersion,
        }),
      });
      if (!response.ok) {
        const payload = await response.json() as { message?: string };
        throw new SetupClientError(payload.message ?? "تعذر حفظ السعر الخاص.");
      }
    },
    async remove(customerId: string): Promise<void> {
      const response = await fetch(`/api/customers/${customerId}`, { method: "DELETE", headers: deviceHeaders(session) });
      if (response.status === 403) throw new SetupClientError("لا توجد صلاحية لحذف الزبون.");
      if (!response.ok) {
        const payload = await response.json().catch(() => ({})) as { message?: string };
        throw new SetupClientError(payload.message ?? "تعذر حذف الزبون.");
      }
    },
    async get(customerId: string): Promise<CustomerListItem | null> {
      const response = await fetch(`/api/customers/${customerId}`, { headers: deviceHeaders(session) });
      if (response.status === 404) return null;
      if (!response.ok) throw new SetupClientError("تعذر تحميل بيانات الزبائن");
      return mapCustomer(await response.json() as CustomerPayload);
    },
  };
}
