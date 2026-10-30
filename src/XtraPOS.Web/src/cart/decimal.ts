const SCALE = 28n;
const FACTOR = 10n ** SCALE;

export function parseDecimal(value: string): bigint {
  const text = value.trim();
  if (!/^-?\d+(\.\d+)?$/.test(text)) {
    throw new Error("القيمة العشرية غير صالحة.");
  }

  const negative = text.startsWith("-");
  const [whole, fraction = ""] = text.replace("-", "").split(".");
  const scaled = BigInt(whole) * FACTOR + BigInt((fraction + "0".repeat(Number(SCALE))).slice(0, Number(SCALE)));
  return negative ? -scaled : scaled;
}

export function formatDecimal(value: bigint): string {
  const negative = value < 0n;
  const absolute = negative ? -value : value;
  const whole = absolute / FACTOR;
  const fraction = (absolute % FACTOR).toString().padStart(Number(SCALE), "0").replace(/0+$/, "");
  const text = fraction.length > 0 ? `${whole}.${fraction}` : `${whole}`;
  return negative ? `-${text}` : text;
}

export function addDecimals(left: string, right: string): string {
  return formatDecimal(parseDecimal(left) + parseDecimal(right));
}

export function multiplyDecimals(left: string, right: string): string {
  return formatDecimal((parseDecimal(left) * parseDecimal(right)) / FACTOR);
}

export function divideDecimals(left: string, right: string): string {
  const divisor = parseDecimal(right);
  if (divisor === 0n) {
    throw new Error("لا يمكن القسمة على صفر.");
  }
  return formatDecimal((parseDecimal(left) * FACTOR) / divisor);
}

export function subtractDecimals(left: string, right: string): string {
  return formatDecimal(parseDecimal(left) - parseDecimal(right));
}

export function isPositiveDecimal(value: string): boolean {
  return parseDecimal(value) > 0n;
}

export function isPositiveInteger(value: string): boolean {
  return /^[1-9]\d*$/.test(value.trim());
}

export function formatPosMoney(value: string): string {
  return roundDecimal(value, 2);
}

export function roundDecimal(value: string, places: number): string {
  const parsed = parseDecimal(value);
  const negative = parsed < 0n;
  const absolute = negative ? -parsed : parsed;
  const shift = 10n ** BigInt(SCALE - BigInt(places));
  const rounded = (absolute + shift / 2n) / shift;
  const placeFactor = 10n ** BigInt(places);
  const whole = rounded / placeFactor;
  const fraction = (rounded % placeFactor).toString().padStart(places, "0").replace(/0+$/, "");
  const text = fraction.length > 0 ? `${whole}.${fraction}` : `${whole}`;
  return negative ? `-${text}` : text;
}

export function decimalEquals(left: string, right: string): boolean {
  return parseDecimal(left) === parseDecimal(right);
}

export function compareDecimals(left: string, right: string): number {
  const difference = parseDecimal(left) - parseDecimal(right);
  if (difference === 0n) return 0;
  return difference > 0n ? 1 : -1;
}
