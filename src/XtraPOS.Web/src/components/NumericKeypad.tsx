export function NumericKeypad({
  allowDecimal = false,
  onDigit,
  onClear,
}: {
  allowDecimal?: boolean;
  onDigit: (digit: string) => void;
  onClear: () => void;
}) {
  const keys = allowDecimal
    ? ["1", "2", "3", "4", "5", "6", "7", "8", "9", "مسح", "0", "."]
    : ["1", "2", "3", "4", "5", "6", "7", "8", "9", "مسح", "0"];

  return (
    <div className={allowDecimal ? "pos-pad pos-num-pad numeric-keypad is-decimal" : "pos-pad pos-num-pad numeric-keypad"} dir="ltr">
      {keys.map((key) => (
        <button
          key={key}
          type="button"
          className={key === "مسح" ? "pos-num-clear numeric-keypad-key" : "numeric-keypad-key"}
          onMouseDown={(event) => event.preventDefault()}
          onClick={() => {
            if (key === "مسح") { onClear(); return; }
            onDigit(key);
          }}
        >{key}</button>
      ))}
    </div>
  );
}
