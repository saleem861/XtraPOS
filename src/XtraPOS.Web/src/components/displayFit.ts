import { useEffect, useSyncExternalStore } from "react";

/**
 * ملاءمة شاشة البيع لمساحة الجهاز (ملاحظة المالك 27 نوفمبر 2026).
 * بعض الأجهزة (لابتوب 1366×768، أو شاشة 1920×1080 بتكبير ويندوز 125%/150%) مساحتها الفعلية أصغر من التصميم،
 * فكانت الأجزاء تتداخل حتى يصغّر المالك الزوم يدوياً إلى 75%. التصميم المرجعي هو تلك اللقطة، والبرنامج يصغّر الآن
 * تلقائياً بالنسبة اللازمة (مثل زوم المتصفح) على الكمبيوتر فقط. الموبايل والتابلت كما هما.
 */
export const FIT_REFERENCE = { width: 1880, height: 900 } as const;
export const FIT_MIN = 0.6;

export type DisplayScale = "auto" | "1" | "0.9" | "0.8" | "0.75";
export const DISPLAY_SCALES: DisplayScale[] = ["auto", "1", "0.9", "0.8", "0.75"];

/** النسبة التي تجعل التصميم المرجعي يتسع للمساحة المتاحة، بين 0.6 و1 ومقرّبة لأقرب 0.01. */
export function computeFit(width: number, height: number): number {
  if (!(width > 0) || !(height > 0)) return 1;
  const fit = Math.min(width / FIT_REFERENCE.width, height / FIT_REFERENCE.height, 1);
  return Math.max(FIT_MIN, Math.floor(fit * 100) / 100);
}

export function resolveScale(setting: DisplayScale, width: number, height: number): number {
  return setting === "auto" ? computeFit(width, height) : Number(setting);
}

export function nextScale(current: DisplayScale): DisplayScale {
  return DISPLAY_SCALES[(DISPLAY_SCALES.indexOf(current) + 1) % DISPLAY_SCALES.length];
}

export function scaleLabel(setting: DisplayScale, applied: number): string {
  const percent = `${Math.round(applied * 100)}%`;
  return setting === "auto" ? `تلقائي (${percent})` : percent;
}

const STORAGE_KEY = "xtrapos.displayScale";
const listeners = new Set<() => void>();

function readStored(): DisplayScale {
  try {
    const value = window.localStorage.getItem(STORAGE_KEY) as DisplayScale | null;
    return value && DISPLAY_SCALES.includes(value) ? value : "auto";
  } catch {
    return "auto";
  }
}

let setting: DisplayScale = typeof window === "undefined" ? "auto" : readStored();

export function setDisplayScale(next: DisplayScale) {
  setting = next;
  try {
    window.localStorage.setItem(STORAGE_KEY, next);
  } catch {
    // بلا تخزين يبقى الاختيار لهذه الجلسة فقط.
  }
  for (const listener of listeners) listener();
}

export function useDisplayScale(): DisplayScale {
  return useSyncExternalStore(
    (listener) => { listeners.add(listener); return () => { listeners.delete(listener); }; },
    () => setting,
    () => "auto" as DisplayScale,
  );
}

/**
 * يطبّق النسبة على كل التطبيق (`zoom` على `.app`، فيبقى النقر دقيقاً) ما دامت شاشة البيع مفتوحة على الكمبيوتر.
 * الارتفاع يُعطى بالبكسل مقسوماً على النسبة، فيملأ الشاشة تماماً بعد التصغير.
 */
export function useDesktopFit(active: boolean): number {
  const current = useDisplayScale();
  useEffect(() => {
    if (!active) return undefined;
    const root = document.documentElement;
    const apply = () => {
      const scale = resolveScale(current, window.innerWidth, window.innerHeight);
      root.classList.toggle("pos-fit", scale < 1);
      root.style.setProperty("--pos-fit", String(scale));
      root.style.setProperty("--pos-fit-height", `${Math.floor(window.innerHeight / scale)}px`);
    };
    apply();
    window.addEventListener("resize", apply);
    return () => {
      window.removeEventListener("resize", apply);
      root.classList.remove("pos-fit");
      root.style.removeProperty("--pos-fit");
      root.style.removeProperty("--pos-fit-height");
    };
  }, [active, current]);
  return active && typeof window !== "undefined" ? resolveScale(current, window.innerWidth, window.innerHeight) : 1;
}
