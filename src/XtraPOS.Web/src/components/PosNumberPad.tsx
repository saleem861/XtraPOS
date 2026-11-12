export function PosNumberPad({ onKey }: { onKey: (key: string) => void }) {
  const keys = ["Del", "/", "*", "BS", "7", "8", "9", "-", "4", "5", "6", "+", "1", "2", "3", "Enter", "0", "."];
  return (
    <div className="pos-side-pad" dir="ltr">
      {keys.map((key) => (
        <button
          key={key}
          type="button"
          className={key === "Enter" ? "is-enter" : key === "0" ? "is-wide" : undefined}
          onMouseDown={(event) => event.preventDefault()}
          aria-label={key === "Enter" ? "إدخال" : `مفتاح ${key}`}
          onClick={() => onKey(key)}
        >{key === "Enter" ? "إدخال" : key}</button>
      ))}
    </div>
  );
}
