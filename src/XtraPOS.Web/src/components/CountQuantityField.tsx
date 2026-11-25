import { useEffect, useRef, useState, type InputHTMLAttributes } from "react";
import { usesDesktopCustomerKeyboard } from "../customers/newCustomerForm";
import { normalizeDigits, stepQuantity } from "../inventory/inventoryRules";
import { useFullKeyboardVisible } from "./keyboardVisibility";
import { PosNumberPad } from "./PosNumberPad";

/**
 * قاعدة المالك لمربع الكمية في كل شاشات الجرد:
 * - الكمية الافتراضية صفر باهت (placeholder)، والحفظ بلا كتابة يعني صفراً.
 * - اللابتوب: لوحة أرقام على الشاشة مثل لوحة أرقام الكمية في شاشة البيع، تظهر ما دام المربع مُركَّزاً.
 * - أندرويد/iOS: لوحة أرقام الجهاز (inputMode) وتتحرك الصفحة ليبقى المربع ظاهراً فوقها.
 */
export function applyPadKey(current: string, key: string): string | "enter" {
  if (key === "Enter") return "enter";
  if (key === "BS") return current.slice(0, -1);
  if (key === "Del") return "";
  if (key === "+") return stepQuantity(current, 1);
  if (key === "-") return stepQuantity(current, -1);
  if (key === ".") return current.includes(".") ? current : `${current || "0"}.`;
  if (/^\d$/.test(key)) return normalizeDigits(current === "0" ? key : current + key);
  return current;
}

/** يُبقي المربع ظاهراً فوق لوحة مفاتيح الجهاز (التي تقلّص المساحة المرئية). */
function keepVisible(input: HTMLInputElement | null) {
  if (!input || typeof input.scrollIntoView !== "function") return;
  try {
    input.scrollIntoView({ block: "center", behavior: "smooth" });
  } catch {
    // متصفح قديم لا يقبل الخيارات.
  }
}

export function CountQuantityField({
  value,
  onChange,
  onEnter,
  label,
  ...rest
}: {
  value: string;
  onChange: (value: string) => void;
  onEnter: () => void;
  label: string;
} & Omit<InputHTMLAttributes<HTMLInputElement>, "value" | "onChange" | "inputMode" | "placeholder">) {
  const desktop = usesDesktopCustomerKeyboard();
  const [fullKeyboard] = useFullKeyboardVisible();
  const [focused, setFocused] = useState(false);
  const ref = useRef<HTMLInputElement>(null);

  useEffect(() => {
    if (!focused) return undefined;
    // اللابتوب: لوحة الأرقام ثابتة أسفل الشاشة، فيُوسَّط المربع فوقها مرة واحدة.
    if (desktop) {
      const timer = window.setTimeout(() => keepVisible(ref.current), 50);
      return () => window.clearTimeout(timer);
    }
    const viewport = window.visualViewport;
    const timer = window.setTimeout(() => keepVisible(ref.current), 300);
    const onResize = () => { if (document.activeElement === ref.current) keepVisible(ref.current); };
    viewport?.addEventListener("resize", onResize);
    return () => {
      window.clearTimeout(timer);
      viewport?.removeEventListener("resize", onResize);
    };
  }, [desktop, focused]);

  function press(key: string) {
    const next = applyPadKey(value, key);
    if (next === "enter") onEnter();
    else onChange(next);
  }

  return (
    <>
      <input
        {...rest}
        ref={ref}
        inputMode="decimal"
        placeholder="0"
        className={["inventory-qty-input", rest.className].filter(Boolean).join(" ")}
        aria-label={label}
        value={value}
        onChange={(event) => onChange(event.target.value)}
        onFocus={(event) => { setFocused(true); rest.onFocus?.(event); }}
        onBlur={(event) => { setFocused(false); rest.onBlur?.(event); }}
        onKeyDown={(event) => {
          if (event.key === "Enter") { event.preventDefault(); onEnter(); return; }
          rest.onKeyDown?.(event);
        }}
      />
      {desktop && focused && !fullKeyboard ? (
        <div className="inventory-numpad" role="group" aria-label="لوحة الأرقام">
          <PosNumberPad onKey={press} />
        </div>
      ) : null}
    </>
  );
}
