import { describe, expect, it } from "vitest";
import {
  CART_STORAGE_KEY,
  MAX_QUANTITY,
  addToCart,
  cartSubtotal,
  itemCount,
  readCart,
  removeFromCart,
  setLineQuantity,
} from "./cart.ts";

describe("cart", () => {
  it("adds a formula and merges a second add", () => {
    const once = addToCart([], "night-clinic", 1);
    const twice = addToCart(once, "night-clinic", 2);
    expect(twice).toEqual([{ slug: "night-clinic", quantity: 3 }]);
    expect(itemCount(twice)).toBe(3);
  });

  it("caps a line at the house maximum", () => {
    const lines = addToCart([{ slug: "resin-hour", quantity: 5 }], "resin-hour", 4);
    expect(lines[0]?.quantity).toBe(MAX_QUANTITY);
  });

  it("ignores an empty add and drops a line at zero", () => {
    const lines = [{ slug: "salt-ledger", quantity: 2 }];
    expect(addToCart(lines, "salt-ledger", 0)).toEqual(lines);
    expect(setLineQuantity(lines, "salt-ledger", 0)).toEqual([]);
    expect(removeFromCart(lines, "salt-ledger")).toEqual([]);
  });

  it("prices only formulas that are still in the cabinet", () => {
    const total = cartSubtotal(
      [
        { slug: "citrus-ward", quantity: 2 },
        { slug: "retired", quantity: 1 },
      ],
      (slug) => (slug === "citrus-ward" ? 78 : undefined),
    );
    expect(total).toBe(156);
  });

  it("reads a stored cart and drops damaged lines", () => {
    localStorage.setItem(
      CART_STORAGE_KEY,
      JSON.stringify([
        { slug: "night-clinic", quantity: 2 },
        { slug: "", quantity: 1 },
        { slug: "too-many", quantity: 99 },
        { nope: true },
      ]),
    );
    expect(readCart(localStorage)).toEqual([{ slug: "night-clinic", quantity: 2 }]);
    localStorage.setItem(CART_STORAGE_KEY, "{");
    expect(readCart(localStorage)).toEqual([]);
  });
});
