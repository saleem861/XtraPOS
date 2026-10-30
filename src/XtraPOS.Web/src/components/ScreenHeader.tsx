import { ArrowLeft } from "lucide-react";

export function ScreenHeader({ title, context, onBack }: {
  title: string;
  context?: string;
  onBack?: () => void;
}) {
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
