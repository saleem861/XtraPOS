import { describe, expect, it } from "vitest";
import { sortCustomers } from "./customerSearch";
import type { CustomerListItem } from "../api/customers";

const customer = (patch: Partial<CustomerListItem>): CustomerListItem => ({
  customerId: "id",
  customerNumber: "1",
  name: "أحمد",
  phone: "0599",
  classificationName: "عادي",
  priceCode: "Price0",
  variableValueDiscountPercent: "0",
  ...patch,
});

describe("customer search order", () => {
  it("keeps server order until a column is chosen", () => {
    const rows = [customer({ customerId: "b", customerNumber: "20", name: "هدية" }), customer({ customerId: "a", customerNumber: "3", name: "أحمد" })];
    expect(rows.map((row) => row.customerId)).toEqual(["b", "a"]);
  });

  it("sorts by customer number only after a header click", () => {
    const rows = [customer({ customerNumber: "20" }), customer({ customerNumber: "3" })];
    expect(sortCustomers(rows, "number", "asc").map((row) => row.customerNumber)).toEqual(["20", "3"]);
  });
});
