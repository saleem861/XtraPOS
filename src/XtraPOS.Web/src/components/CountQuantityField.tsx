import { useEffect, useRef, useState, type InputHTMLAttributes } from "react";
import { usesDesktopCustomerKeyboard } from "../customers/newCustomerForm";
import { normalizeDigits, stepQuantity } from "../inventory/inventoryRules";
import { NumericEditModal } from "./NumericEditModal";

/**
 * قاعدة المالك لمربع الكمية في كل شاشات الجرد:
 * - الكمية الافتراضية صفر باهت (placeholder)، والحفظ بلا كتابة يعني صفراً.
 * - الكمبيوتر: عند الوصول للمربع (نقر أو انتقال تلقائي بعد حفظ الصنف السابق) تظهر لوحة أرقام
 *   في منتصف الشاشة، بالضبط مثل لوحة «الكمية» في شاشة البيع؛ «تأكيد» أو Enter يحفظ الصنف (المالك 27 نوفمبر).
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
  title,
  saveOnConfirm = true,
  ...rest
}: {
  value: string;
  onChange: (value: string) => void;
  onEnter: () => void;
  label: string;
  /** عنوان لوحة الأرقام على الكمبيوتر (اسم الصنف). الافتراض: label. */
  title?: string;
  /** «تأكيد» في لوحة الكمبيوتر يحفظ مثل Enter (الجرد). false: يكتب الكمية فقط (التصحيح يحتاج سبباً بعدها). */
  saveOnConfirm?: boolean;
} & Omit<InputHTMLAttributes<HTMLInputElement>, "value" | "onChange" | "inputMode" | "placeholder">) {
  const desktop = usesDesktopCustomerKeyboard();
  const [focused, setFocused] = useState(false);
  const [padOpen, setPadOpen] = useState(false);
  const ref = useRef<HTMLInputElement>(null);
  // بعد «تأكيد» تتغير القيمة عند الأب أولاً، ثم يُحفظ بأحدث نسخة من onEnter (التي ترى القيمة الجديدة).
  const latestEnter = useRef(onEnter);
  latestEnter.current = onEnter;

  useEffect(() => {
    if (desktop || !focused) return undefined;
    const viewport = window.visualViewport;
    const timer = window.setTimeout(() => keepVisible(ref.current), 300);
    const onResize = () => { if (document.activeElement === ref.current) keepVisible(ref.current); };
    viewport?.addEventListener("resize", onResize);
    return () => {
      window.clearTimeout(timer);
      viewport?.removeEventListener("resize", onResize);
    };
  }, [desktop, focused]);

  function openPad() {
    if (!desktop || rest.disabled) return;
    keepVisible(ref.current);
    setPadOpen(true);
  }

  return (
    <>
      <input
        {...rest}
        ref={ref}
        inputMode={desktop ? "none" : "decimal"}
        readOnly={desktop || rest.readOnly}
        placeholder="0"
        className={["inventory-qty-input", rest.className].filter(Boolean).join(" ")}
        aria-label={label}
        value={value}
        onChange={(event) => onChange(event.target.value)}
        onFocus={(event) => { setFocused(true); openPad(); rest.onFocus?.(event); }}
        onBlur={(event) => { setFocused(false); rest.onBlur?.(event); }}
        onClick={(event) => { if (!padOpen) openPad(); rest.onClick?.(event); }}
        onKeyDown={(event) => {
          if (event.key === "Enter") { event.preventDefault(); onEnter(); return; }
          rest.onKeyDown?.(event);
        }}
      />
      {padOpen ? (
        <NumericEditModal
          title={title ?? label}
          ariaLabel="لوحة الأرقام"
          inputLabel={label}
          initialValue={value}
          placeholder="0"
          allowDecimal
          error=""
          onConfirm={(next) => {
            setPadOpen(false);
            onChange(next);
            if (saveOnConfirm) window.setTimeout(() => latestEnter.current(), 0);
          }}
          onCancel={() => setPadOpen(false)}
        />
      ) : null}
    </>
  );
}
