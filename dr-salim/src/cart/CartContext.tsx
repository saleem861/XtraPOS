import { createContext, useContext, useEffect, useMemo, useState, type ReactNode } from "react";
import {
  CART_STORAGE_KEY,
  addToCart,
  itemCount,
  readCart,
  removeFromCart,
  setLineQuantity,
  type CartLine,
} from "./cart.ts";

type CartApi = {
  lines: readonly CartLine[];
  count: number;
  add: (slug: string, quantity?: number) => void;
  setQuantity: (slug: string, quantity: number) => void;
  remove: (slug: string) => void;
};

const CartContext = createContext<CartApi | null>(null);

export function CartProvider({ children }: { children: ReactNode }) {
  const [lines, setLines] = useState<CartLine[]>(() => readCart(localStorage));

  useEffect(() => {
    try {
      localStorage.setItem(CART_STORAGE_KEY, JSON.stringify(lines));
    } catch {
      // The selection still works for this visit if storage is blocked.
    }
  }, [lines]);

  const value = useMemo<CartApi>(
    () => ({
      lines,
      count: itemCount(lines),
      add(slug, quantity = 1) {
        setLines((current) => addToCart(current, slug, quantity));
      },
      setQuantity(slug, quantity) {
        setLines((current) => setLineQuantity(current, slug, quantity));
      },
      remove(slug) {
        setLines((current) => removeFromCart(current, slug));
      },
    }),
    [lines],
  );

  return <CartContext.Provider value={value}>{children}</CartContext.Provider>;
}

export function useCart(): CartApi {
  const value = useContext(CartContext);
  if (!value) throw new Error("useCart must be used inside CartProvider");
  return value;
}
