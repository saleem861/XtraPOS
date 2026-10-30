import { ArrowLeft, House } from "lucide-react";

interface AppNavProps {
  onHome: () => void;
  onBack: () => void;
  backDisabled?: boolean;
}

export function AppNav({ onHome, onBack, backDisabled = false }: AppNavProps) {
  return (
    <nav className="app-nav" aria-label="التنقل">
      <button className="nav-button" type="button" onClick={onHome}>
        <House aria-hidden="true" />الرئيسية
      </button>
      <button className="nav-button" type="button" onClick={onBack} disabled={backDisabled}>
        <ArrowLeft aria-hidden="true" />رجوع
      </button>
    </nav>
  );
}
