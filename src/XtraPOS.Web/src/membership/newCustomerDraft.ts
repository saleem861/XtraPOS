export const newCustomerDraftKey = "xtrapos-new-customer-form";
export const pendingEnrollmentKey = "xtrapos-pending-enrollment";

export interface NewCustomerDraft {
  customerNumber: string;
  name: string;
  whatsAppLocal: string;
  countryCode: string;
  classificationId: string;
  priceTypeId: string;
  discount: string;
  notes: string;
}

export function writeNewCustomerDraft(draft: NewCustomerDraft) {
  sessionStorage.setItem(newCustomerDraftKey, JSON.stringify(draft));
}

export function readNewCustomerDraft(): NewCustomerDraft | null {
  const raw = sessionStorage.getItem(newCustomerDraftKey);
  if (!raw) return null;
  try {
    const parsed = JSON.parse(raw) as NewCustomerDraft;
    return parsed?.name ? parsed : null;
  } catch {
    return null;
  }
}

export function clearNewCustomerDraft() {
  sessionStorage.removeItem(newCustomerDraftKey);
  sessionStorage.removeItem(pendingEnrollmentKey);
}
