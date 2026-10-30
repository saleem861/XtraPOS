import type { CustomerListItem } from "../api/customers";

export type CustomerSortColumn = "number" | "name" | "phone" | "type" | "price";

export function sortCustomers(customers: CustomerListItem[], column: CustomerSortColumn, direction: "asc" | "desc"): CustomerListItem[] {
  const factor = direction === "asc" ? 1 : -1;
  const value = (customer: CustomerListItem) => {
    if (column === "number") return customer.customerNumber ?? "";
    if (column === "name") return customer.name;
    if (column === "phone") return customer.phone ?? "";
    if (column === "type") return customer.classificationName ?? "";
    return customer.priceCode ?? "";
  };
  return [...customers].sort((left, right) => value(left).localeCompare(value(right), "ar") * factor);
}
