import { compareDecimals, isPositiveDecimal } from "./decimal";

const SCALE_LIMIT = "300";

export type FastEntryRoute =
  | { kind: "empty" }
  | { kind: "scale"; value: string }
  | { kind: "item"; itemNumber: string }
  | { kind: "invalid"; message: string };

export function sameItemNumber(stored: string, typed: string): boolean {
  const fold = (value: string) => value.trim()
    .replace(/[٠-٩]/g, (digit) => String("٠١٢٣٤٥٦٧٨٩".indexOf(digit)))
    .replace(/^0+(?=\d)/, "");
  return stored.trim() === typed.trim() || fold(stored) === fold(typed);
}

export function interpretFastEntry(raw: string): FastEntryRoute {
  const text = raw.trim();
  if (!text) return { kind: "empty" };

  if (text.startsWith("#")) {
    const itemNumber = text.slice(1).trim();
    if (!itemNumber) return { kind: "invalid", message: "أدخل رقم الصنف بعد #." };
    return { kind: "item", itemNumber };
  }

  if (/^\d+(?:\.\d+)?$/.test(text)) {
    if (!isPositiveDecimal(text)) {
      return { kind: "invalid", message: "قيمة الميزان يجب أن تكون أكبر من صفر." };
    }

    if (compareDecimals(text, SCALE_LIMIT) <= 0) {
      return { kind: "scale", value: text };
    }

    return { kind: "item", itemNumber: text };
  }

  return { kind: "item", itemNumber: text };
}
