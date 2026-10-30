import { useEffect, useState, type FormEvent, type KeyboardEvent } from "react";
import { NumericKeypad } from "./NumericKeypad";

function prefersFinePointer(): boolean {
  if (typeof window.matchMedia !== "function") return true;
  return window.matchMedia("(pointer: fine)").matches;
}

function appendDigit(current: string, digit: string, fresh: boolean, allowDecimal: boolean): string {
  const base = fresh ? "" : current;
  if (digit === ".") {
    if (!allowDecimal || base.includes(".")) return base || (allowDecimal ? "0" : "");
    return `${base || "0"}.`;
  }
  const next = `${base}${digit}`.replace(/^0+(?=\d)/, "");
  if (!allowDecimal) return next;
  const fraction = next.split(".")[1];
  if ((fraction?.length ?? 0) > 2) return base;
  return next;
}

export function NumericEditModal({
  title,
  ariaLabel,
  inputLabel,
  initialValue,
  allowDecimal,
  error,
  resetLabel,
  onConfirm,
  onCancel,
  onReset,
}: {
  title: string;
  ariaLabel: string;
  inputLabel: string;
  initialValue: string;
  allowDecimal: boolean;
  error: string;
  resetLabel?: string;
  onConfirm: (value: string) => void;
  onCancel: () => void;
  onReset?: () => void;
}) {
  const [finePointer, setFinePointer] = useState(prefersFinePointer);
  const [value, setValue] = useState(initialValue);
  const [fresh, setFresh] = useState(true);
  const [frame, setFrame] = useState<{ top: number; height: number } | null>(null);

  useEffect(() => {
    if (typeof window.matchMedia !== "function") return;
    const query = window.matchMedia("(pointer: fine)");
    const apply = () => setFinePointer(query.matches);
    query.addEventListener("change", apply);
    return () => query.removeEventListener("change", apply);
  }, []);

  useEffect(() => {
    if (finePointer || !window.visualViewport) return;
    const viewport = window.visualViewport;
    const apply = () => setFrame({ top: viewport.offsetTop, height: viewport.height });
    apply();
    viewport.addEventListener("resize", apply);
    viewport.addEventListener("scroll", apply);
    return () => {
      viewport.removeEventListener("resize", apply);
      viewport.removeEventListener("scroll", apply);
    };
  }, [finePointer]);

  function push(digit: string) {
    setValue((current) => appendDigit(current, digit, fresh, allowDecimal));
    setFresh(false);
  }

  function onKeyDown(event: KeyboardEvent<HTMLFormElement>) {
    if (event.key === "Enter") {
      event.preventDefault();
      onConfirm(value);
      return;
    }
    if (event.key === "Escape") {
      event.preventDefault();
      onCancel();
      return;
    }
    if (!finePointer) return;
    const numpad = /^Numpad([0-9])$/.exec(event.code);
    if (/^[0-9]$/.test(event.key) || numpad) {
      event.preventDefault();
      push(numpad ? numpad[1] : event.key);
      return;
    }
    if (allowDecimal && (event.key === "." || event.key === "," || event.code === "NumpadDecimal")) {
      event.preventDefault();
      push(".");
      return;
    }
    if (event.key === "Backspace" || event.key === "Delete") {
      event.preventDefault();
      setFresh(false);
      setValue((current) => event.key === "Delete" ? "" : current.slice(0, -1));
      return;
    }
    if (event.key.length === 1) event.preventDefault();
  }

  function onInput(next: string) {
    const normalized = next.replace(",", ".");
    if (allowDecimal) {
      if (!/^\d*\.?\d{0,2}$/.test(normalized)) return;
    } else if (!/^\d*$/.test(normalized)) return;
    setValue(normalized.replace(/^0+(?=\d)/, ""));
    setFresh(false);
  }

  return (
    <div
      className={finePointer ? "pos-overlay" : "pos-overlay pos-overlay-top pos-num-overlay"}
      style={frame ? { top: frame.top, height: frame.height, bottom: "auto" } : undefined}
    >
      <form
        className={`pos-modal pos-qty-modal pos-num-modal${allowDecimal ? " pos-num-money" : ""}${finePointer ? "" : " pos-num-touch"}`}
        role="dialog"
        aria-label={ariaLabel}
        onSubmit={(event: FormEvent) => { event.preventDefault(); onConfirm(value); }}
        onKeyDown={onKeyDown}
      >
        {finePointer ? <button className="pos-num-close" type="button" aria-label="إغلاق" onClick={onCancel}>×</button> : null}
        <h3 className="pos-qty-title">{title}</h3>
        <input
          className="pos-qty-figure"
          aria-label={inputLabel}
          type="text"
          inputMode={finePointer ? "none" : allowDecimal ? "decimal" : "numeric"}
          readOnly={finePointer}
          autoFocus
          value={value}
          onFocus={(event) => { if (fresh) event.currentTarget.select(); }}
          onChange={(event) => onInput(event.target.value)}
        />
        {error ? <p className="status-error">{error}</p> : null}
        {finePointer ? (
          <NumericKeypad
            allowDecimal={allowDecimal}
            onDigit={push}
            onClear={() => { setValue(""); setFresh(false); }}
          />
        ) : null}
        <div className="pos-modal-actions pos-qty-actions">
          <button className="primary-button" type="submit">تأكيد</button>
          <button className="nav-button" type="button" onClick={onCancel}>إلغاء</button>
        </div>
        {resetLabel && onReset ? <button className="pos-num-reset" type="button" onClick={onReset}>{resetLabel}</button> : null}
      </form>
    </div>
  );
}
