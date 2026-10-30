import { useState } from "react";

const letterRows = [
  ["ض", "ص", "ث", "ق", "ف", "غ", "ع", "ه", "خ", "ح", "ج"],
  ["ش", "س", "ي", "ب", "ل", "ا", "ت", "ن", "م", "ك", "ط"],
  ["ئ", "ء", "ؤ", "ر", "لا", "ى", "ة", "و", "ز", "ظ"],
];

const shiftRows = [
  ["أ", "إ", "آ", "ة", "ى", "ئ", "ء", "ؤ", "ـ", "لا", "ذ"],
  ["د", "ذ", "ر", "ز", "و", "ة", "ى", "ء", "ئ", "ؤ", "أ"],
  ["،", "؛", ":", "!", "؟", ".", "-", "/", "(", ")"],
];

const symbolRows = [
  ["١", "٢", "٣", "٤", "٥", "٦", "٧", "٨", "٩", "٠", "٪"],
  ["@", "#", "&", "*", "(", ")", "-", "+", "=", "/", "\\"],
  ["،", ".", "؟", "!", ":", "؛", "«", "»", "'", "\""],
];

function writeFocused(text: string, remove = false) {
  const field = document.activeElement;
  if (!(field instanceof HTMLInputElement || field instanceof HTMLTextAreaElement) || field.readOnly) return;
  const start = field.selectionStart ?? field.value.length;
  const end = field.selectionEnd ?? field.value.length;
  const from = remove && start === end ? Math.max(0, start - 1) : start;
  const next = field.value.slice(0, from) + (remove ? "" : text) + field.value.slice(end);
  const prototype = field instanceof HTMLTextAreaElement ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
  const descriptor = Object.getOwnPropertyDescriptor(prototype, "value");
  descriptor?.set?.call(field, next);
  field.dispatchEvent(new Event("input", { bubbles: true }));
  const caret = from + (remove ? 0 : text.length);
  field.setSelectionRange(caret, caret);
}

export function FullKeyboard({ onEnter }: { onEnter: () => void }) {
  const [layer, setLayer] = useState<"letters" | "shift" | "symbols">("letters");
  const rows = layer === "symbols" ? symbolRows : layer === "shift" ? shiftRows : letterRows;
  return (
    <div className="new-customer-keys full-keyboard-screen-keyboard" dir="ltr" aria-label="لوحة المفاتيح">
      <div className="nc-letters">
        {rows.map((row) => (
          <div key={row.join("")} style={{ gridTemplateColumns: `repeat(${row.length}, minmax(0, 1fr))` }}>
            {row.map((key, index) => <button key={`${key}-${index}`} type="button" onMouseDown={(event) => event.preventDefault()} onClick={() => writeFocused(key)}>{key}</button>)}
          </div>
        ))}
        <div className="is-actions">
          <button type="button" className="is-special" onMouseDown={(event) => event.preventDefault()} onClick={() => setLayer((current) => current === "shift" ? "letters" : "shift")}>Shift</button>
          <button type="button" className="is-special" onMouseDown={(event) => event.preventDefault()} onClick={() => setLayer((current) => current === "symbols" ? "letters" : "symbols")}>رموز</button>
          <button type="button" className="is-space" onMouseDown={(event) => event.preventDefault()} onClick={() => writeFocused(" ")}>مسافة</button>
          <button type="button" className="is-special" onMouseDown={(event) => event.preventDefault()} onClick={() => writeFocused("", true)}>⌫</button>
          <button type="button" className="is-enter" onMouseDown={(event) => event.preventDefault()} onClick={onEnter}>إدخال</button>
        </div>
      </div>
      <div className="nc-pad" dir="ltr">
        {["7", "8", "9", "÷", "4", "5", "6", "×", "1", "2", "3", "−", ".", "0", "00", "+"].map((key) => (
          <button key={key} type="button" className={"÷×−+".includes(key) ? "is-op" : undefined} onMouseDown={(event) => event.preventDefault()} onClick={() => writeFocused(key)}>{key}</button>
        ))}
      </div>
    </div>
  );
}
