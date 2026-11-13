export const CART_STORAGE_KEY = "dr-salim.cart.v1";
export const MAX_QUANTITY = 6;

export type CartLine = {
  slug: string;
  quantity: number;
};

export function addToCart(lines: readonly CartLine[], slug: string, quantity = 1): CartLine[] {
  const delta = Math.floor(quantity);
  if (!Number.isFinite(delta) || delta <= 0) return [...lines];
  const existing = lines.find((line) => line.slug === slug);
  if (!existing) return [...lines, { slug, quantity: clamp(delta) }];
  return lines.map((line) =>
    line.slug === slug ? { ...line, quantity: clamp(line.quantity + delta) } : line,
  );
}

export function setLineQuantity(lines: readonly CartLine[], slug: string, quantity: number): CartLine[] {
  if (quantity <= 0) return removeFromCart(lines, slug);
  return lines.map((line) => (line.slug === slug ? { ...line, quantity: clamp(quantity) } : line));
}

export function removeFromCart(lines: readonly CartLine[], slug: string): CartLine[] {
  return lines.filter((line) => line.slug !== slug);
}

export function itemCount(lines: readonly CartLine[]): number {
  return lines.reduce((sum, line) => sum + line.quantity, 0);
}

export function cartSubtotal(
  lines: readonly CartLine[],
  priceOf: (slug: string) => number | undefined,
): number {
  return lines.reduce((sum, line) => sum + (priceOf(line.slug) ?? 0) * line.quantity, 0);
}

export function readCart(storage: Storage): CartLine[] {
  try {
    const raw = storage.getItem(CART_STORAGE_KEY);
    if (!raw) return [];
    const parsed: unknown = JSON.parse(raw);
    if (!Array.isArray(parsed)) return [];
    return parsed.filter(isCartLine);
  } catch {
    return [];
  }
}

function clamp(quantity: number): number {
  return Math.max(1, Math.min(MAX_QUANTITY, Math.floor(quantity)));
}

function isCartLine(value: unknown): value is CartLine {
  if (!value || typeof value !== "object") return false;
  const line = value as { slug?: unknown; quantity?: unknown };
  return (
    typeof line.slug === "string" &&
    line.slug.length > 0 &&
    line.slug.length < 80 &&
    typeof line.quantity === "number" &&
    Number.isInteger(line.quantity) &&
    line.quantity >= 1 &&
    line.quantity <= MAX_QUANTITY
  );
}
