import { describe, expect, it } from "vitest";
import { clearNewCustomerDraft, pendingEnrollmentKey, readNewCustomerDraft, writeNewCustomerDraft } from "./newCustomerDraft";

describe("new customer enrollment draft", () => {
  it("keeps the form across the provider redirect and clears it after save", () => {
    writeNewCustomerDraft({ customerNumber: "11", name: "زبون", whatsAppLocal: "599111111", countryCode: "970", classificationId: "c", priceTypeId: "p", discount: "0", notes: "" });
    sessionStorage.setItem(pendingEnrollmentKey, "attempt-1");
    expect(readNewCustomerDraft()?.name).toBe("زبون");
    expect(sessionStorage.getItem(pendingEnrollmentKey)).toBe("attempt-1");
    clearNewCustomerDraft();
    expect(readNewCustomerDraft()).toBeNull();
    expect(sessionStorage.getItem(pendingEnrollmentKey)).toBeNull();
  });
});