import { ArrowLeft } from "lucide-react";
import { useLayoutEffect, useState } from "react";
import { createPortal } from "react-dom";

export function ScreenHeader({ title, context, onBack }: {
  title: string;
  context?: string;
  onBack?: () => void;
}) {
  const [titleSlot, setTitleSlot] = useState<HTMLElement | null>(null);
  const [backSlot, setBackSlot] = useState<HTMLElement | null>(null);

  useLayoutEffect(() => {
    setTitleSlot(document.getElementById("app-title-slot"));
    setBackSlot(document.getElementById("app-back-slot"));
  }, []);

  if (titleSlot) {
    return (
      <>
        {createPortal(<span>{title}</span>, titleSlot)}
        {onBack && backSlot ? createPortal(
          <button type="button" className="xp-screen-back" onClick={onBack}><ArrowLeft aria-hidden="true" />رجوع</button>,
          backSlot,
        ) : null}
      </>
    );
  }

  return (
    <header className="xp-screen-bar">
      <div className="xp-screen-brand">
        <strong>XtraPOS</strong>
        {context ? <span>{context}</span> : null}
      </div>
      <h1>{title}</h1>
      {onBack ? <button type="button" className="xp-screen-back" onClick={onBack}><ArrowLeft aria-hidden="true" />رجوع</button> : <span />}
    </header>
  );
}
