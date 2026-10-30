import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { useState } from "react";
import { describe, expect, it } from "vitest";
import type { SpecialPriceRow } from "../api/customers";
import type { ItemListItem } from "../catalog/types";
import { ExceptionalPricesScreen, targetUnitPrice } from "./ExceptionalPricesScreen";

const item = { itemId: "item", itemNumber: "100", itemName: "حليب" } as ItemListItem;

function Harness() {
  const [rows, setRows] = useState<SpecialPriceRow[]>(Array.from({ length: 12 }, (_, index) => ({
    customerItemSpecialPriceId: String(index),
    itemId: String(index),
    itemNumber: String(index),
    itemName: `صنف ${index}`,
    basePrice: 5.5,
    specialPrice: 4.5,
    minimumQuantity: 10,
    bundlePrice: 45,
    validFrom: null,
    validTo: null,
    isActive: true,
    rowVersion: "v",
  })));
  return (
    <ExceptionalPricesScreen
      customerNumber="1"
      customerName="سليم"
      rows={rows}
      onSearch={async () => [item]}
      currentPrice={async () => "5.50"}
      onCommit={async (row) => {
        setRows((current) => [...current, {
          customerItemSpecialPriceId: "new",
          itemId: row.item.itemId,
          itemNumber: row.item.itemNumber,
          itemName: row.item.itemName,
          basePrice: 5.5,
          specialPrice: Number(row.targetUnitPrice),
          minimumQuantity: Number(row.minimumQuantity),
          bundlePrice: Number(row.bundlePrice),
          validFrom: null,
          validTo: null,
          isActive: true,
          rowVersion: "",
        }]);
      }}
      onDelete={async (row) => setRows((current) => current.filter((entry) => entry.customerItemSpecialPriceId !== row.customerItemSpecialPriceId))}
      onClose={() => undefined}
    />
  );
}

describe("exceptional quantity pricing screen", () => {
  it("calculates the live target and shares one modal for quantity and price", async () => {
    expect(targetUnitPrice("45", "10")).toBe("4.50");
    render(<Harness />);
    expect(screen.getByLabelText("البحث عن صنف")).toBeTruthy();
    expect(document.activeElement).toBe(screen.getByLabelText("البحث عن صنف"));
    const wrap = document.querySelector(".exceptional-table-wrap") as HTMLElement;
    expect(wrap.style.overflowY).toBe("auto");
    fireEvent.change(screen.getByLabelText("البحث عن صنف"), { target: { value: "100" } });
    fireEvent.click(await screen.findByRole("button", { name: /حليب/ }));
    fireEvent.click(screen.getByRole("button", { name: /الكمية/ }));
    expect(screen.getByRole("dialog", { name: "الكمية والسعر" })).toBeTruthy();
    expect(document.activeElement).toBe(screen.getByLabelText("كمية الاستثناء"));
    fireEvent.click(screen.getByRole("button", { name: "إلغاء" }));
    expect(document.activeElement).toBe(screen.getByLabelText("البحث عن صنف"));
    fireEvent.click(screen.getByRole("button", { name: /السعر الاستثنائي الجديد/ }));
    expect(screen.getByRole("dialog", { name: "الكمية والسعر" })).toBeTruthy();
    expect(document.activeElement).toBe(screen.getByLabelText("سعر المجموعة"));
    fireEvent.change(screen.getByLabelText("كمية الاستثناء"), { target: { value: "10" } });
    fireEvent.change(screen.getByLabelText("سعر المجموعة"), { target: { value: "45.00" } });
    expect(screen.getByText(/السعر المستهدف للوحدة: 4.50 شيكل/)).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "تثبيت" }));
    await waitFor(() => expect(document.activeElement).toBe(screen.getByLabelText("البحث عن صنف")));
  });
});
