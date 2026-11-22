import { useEffect } from "react";
import { registerLocalWait } from "./waitingActivity";

// مؤشر انتظار موحّد: حلقة متحركة بألوان التطبيق مع نص يوضح ما يجري.
// block: داخل الصفحة. overlay: فوق محتوى موجود (مثل الحفظ) دون إزالته.
export function Waiting({ label = "يرجى الانتظار", variant = "block", compact = false }: {
  label?: string;
  variant?: "block" | "overlay";
  compact?: boolean;
}) {
  useEffect(() => registerLocalWait(), []);
  return (
    <div className={`xp-wait xp-wait-${variant}${compact ? " is-compact" : ""}`} role="status" aria-live="polite">
      <span className="xp-wait-ring" aria-hidden="true">
        <span />
        <span />
        <span />
      </span>
      <span className="xp-wait-label">{label}</span>
    </div>
  );
}
