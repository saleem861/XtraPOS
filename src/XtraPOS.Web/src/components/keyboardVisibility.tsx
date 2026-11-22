import { useSyncExternalStore } from "react";
import { ChevronDown } from "lucide-react";

// إظهار/إخفاء لوحة المفاتيح الكاملة (ومعها لوحة الأرقام) حالة واحدة لكل الشاشات،
// تُحفظ على هذا الجهاز وتبقى بعد إعادة التشغيل. الافتراض: ظاهرة.
const STORAGE_KEY = "xtrapos.fullKeyboardVisible";
const listeners = new Set<() => void>();

function readStored(): boolean {
  try {
    return window.localStorage.getItem(STORAGE_KEY) !== "0";
  } catch {
    return true;
  }
}

let visible = readStored();

function subscribe(listener: () => void) {
  listeners.add(listener);
  const onStorage = (event: StorageEvent) => {
    if (event.key === STORAGE_KEY) {
      visible = readStored();
      listener();
    }
  };
  window.addEventListener("storage", onStorage);
  return () => {
    listeners.delete(listener);
    window.removeEventListener("storage", onStorage);
  };
}

export function setFullKeyboardVisible(next: boolean) {
  visible = next;
  try {
    window.localStorage.setItem(STORAGE_KEY, next ? "1" : "0");
  } catch {
    // بلا تخزين تبقى الحالة مشتركة خلال هذه الجلسة فقط.
  }
  for (const listener of listeners) listener();
}

export function useFullKeyboardVisible(): [boolean, () => void] {
  const current = useSyncExternalStore(subscribe, () => visible, () => true);
  return [current, () => setFullKeyboardVisible(!current)];
}

export function KeyboardToggle() {
  const [open, toggle] = useFullKeyboardVisible();
  return (
    <button type="button" className="sales-review-keyboard-toggle" aria-pressed={open} onClick={toggle}>
      <ChevronDown aria-hidden="true" style={{ transform: open ? "none" : "rotate(180deg)" }} />
      {open ? "إخفاء لوحة المفاتيح والأرقام" : "إظهار لوحة المفاتيح والأرقام"}
    </button>
  );
}
