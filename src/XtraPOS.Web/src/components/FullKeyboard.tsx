import { useState } from "react";

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

function KeyButton({ label, onPress, special, enter, space }: { label: string; onPress: () => void; special?: boolean; enter?: boolean; space?: boolean }) {
  return <button type="button" className={enter ? "is-enter" : space ? "is-space" : special ? "is-special" : undefined} onMouseDown={(event) => event.preventDefault()} onClick={onPress}>{label}</button>;
}

function writeFocused(text: string, remove = false, clear = false) {
  const field = document.activeElement;
  if (!(field instanceof HTMLInputElement || field instanceof HTMLTextAreaElement) || field.readOnly) return;
  const start = field.selectionStart ?? field.value.length;
  const end = field.selectionEnd ?? field.value.length;
  const from = clear ? 0 : remove && start === end ? Math.max(0, start - 1) : start;
  const next = clear ? "" : field.value.slice(0, from) + (remove ? "" : text) + field.value.slice(end);
  const prototype = field instanceof HTMLTextAreaElement ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
  const descriptor = Object.getOwnPropertyDescriptor(prototype, "value");
  descriptor?.set?.call(field, next);
  field.dispatchEvent(new Event("input", { bubbles: true }));
  const caret = from + (remove ? 0 : text.length);
  field.setSelectionRange(caret, caret);
}

const salesDigitRow = ["،", "1", "2", "3", "4", "5", "6", "7", "8", "9", "0", "-", "="];
const salesSymbolRow = ["~", "!", "@", "#", "$", "%", "^", "&", "*", "(", ")", "_", "+"];
const salesLetterRows = [
  ["ض", "ص", "ث", "ق", "ف", "غ", "ع", "ه", "خ", "ح", "ج", "د"],
  ["ش", "س", "ي", "ب", "ل", "ا", "ت", "ن", "م", "ك", "ة"],
  ["ئ", "ء", "ؤ", "ر", "لا", "ى", "ة", "و", "ز", "ظ"],
];
const salesPad = ["Del", "/", "*", "BS", "7", "8", "9", "-", "4", "5", "6", "+", "1", "2", "3", "Enter", "0", "."];

function pressPad(key: string, onEnter: () => void) {
  if (key === "Enter") onEnter();
  else if (key === "Del") writeFocused("", true, true);
  else if (key === "BS") writeFocused("", true);
  else writeFocused(key);
}

export function FullKeyboard({ onEnter }: { onEnter: () => void }) {
  const [layer, setLayer] = useState<"letters" | "shift" | "symbols">("letters");
  const middle = layer === "symbols" ? symbolRows : layer === "shift" ? shiftRows : salesLetterRows;
  return (
      <div className="new-customer-keys full-keyboard-screen-keyboard sales-keyboard" dir="ltr" aria-label="لوحة المفاتيح">
        <div className="nc-letters">
          <div style={{ gridTemplateColumns: `repeat(${salesDigitRow.length + 1}, minmax(0, 1fr))` }}>
            {salesDigitRow.map((key) => <KeyButton key={key} label={key} onPress={() => writeFocused(key)} />)}
            <KeyButton label="⌫" onPress={() => writeFocused("", true)} />
          </div>
          <div style={{ gridTemplateColumns: `repeat(${salesSymbolRow.length}, minmax(0, 1fr))` }}>
            {salesSymbolRow.map((key) => <KeyButton key={key} label={key} onPress={() => writeFocused(key)} />)}
          </div>
          {middle.slice(0, 2).map((row) => (
            <div key={row.join("")} style={{ gridTemplateColumns: `repeat(${row.length}, minmax(0, 1fr))` }}>
              {row.map((key, index) => <KeyButton key={`${key}-${index}`} label={key} onPress={() => writeFocused(key)} />)}
            </div>
          ))}
          <div className="is-letter-side" style={{ gridTemplateColumns: `0.85fr 0.85fr repeat(${middle[2].length}, minmax(0, 1fr))` }}>
            <KeyButton label="Shift" special onPress={() => setLayer((current) => current === "shift" ? "letters" : "shift")} />
            <KeyButton label="رموز" special onPress={() => setLayer((current) => current === "symbols" ? "letters" : "symbols")} />
            {middle[2].map((key, index) => <KeyButton key={`${key}-${index}`} label={key} onPress={() => writeFocused(key)} />)}
          </div>
          <div className="is-sales-actions">
            <button type="button" className="is-special is-lang" onMouseDown={(event) => event.preventDefault()} onClick={() => setLayer("letters")}>اللغة</button>
            <button type="button" className="is-special is-case" onMouseDown={(event) => event.preventDefault()} onClick={() => setLayer((current) => current === "shift" ? "letters" : "shift")}>حروف كبيرة<span>Aa</span></button>
            <KeyButton label="مسافة" space onPress={() => writeFocused(" ")} />
            <KeyButton label="^" special onPress={() => writeFocused("^")} />
          </div>
        </div>
        <div className="nc-pad sales-pad" dir="ltr">
          {salesPad.map((key) => (
            <button key={key} type="button" className={key === "Enter" ? "is-enter" : key === "0" ? "is-wide" : undefined} onMouseDown={(event) => event.preventDefault()} onClick={() => pressPad(key, onEnter)}>{key === "Enter" ? "إدخال" : key}</button>
          ))}
        </div>
      </div>
  );
}
