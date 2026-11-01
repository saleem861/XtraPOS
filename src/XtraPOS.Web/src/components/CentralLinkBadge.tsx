import { Check } from "lucide-react";
import { useEffect, useState } from "react";

type LinkState = "Checking" | "Connected" | "Disconnected";

const labels: Record<LinkState, string> = {
  Checking: "جاري الاتصال بالمركز...",
  Connected: "متصل بالإدارة المركزية",
  Disconnected: "غير متصل بالإدارة المركزية",
};

export function CentralLinkBadge() {
  const [state, setState] = useState<LinkState>("Checking");

  useEffect(() => {
    let stopped = false;
    async function load() {
      try {
        const response = await fetch("/api/central/connection");
        if (!response.ok) {
          if (!stopped) setState("Disconnected");
          return;
        }
        const body = await response.json() as { state?: string };
        if (stopped) return;
        setState(body.state === "Connected" ? "Connected" : "Disconnected");
      } catch {
        if (!stopped) setState("Disconnected");
      }
    }
    void load();
    const timer = window.setInterval(() => void load(), 30000);
    return () => {
      stopped = true;
      window.clearInterval(timer);
    };
  }, []);

  return (
    <span className={`central-link central-link-${state.toLowerCase()}`} role="status">
      {state === "Connected" ? <Check aria-hidden="true" /> : null}
      {labels[state]}
    </span>
  );
}
