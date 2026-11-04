import { useEffect, useState } from "react";

export function MyLogPage() {
  const [text, setText] = useState("جاري قراءة myLog.txt");
  useEffect(() => {
    let stop = false;
    const load = () => {
      void fetch("/api/diagnostics/log", { cache: "no-store" }).then(async (response) => {
        if (stop) return;
        setText(response.ok ? (await response.text() || "السجل فارغ.") : "تعذر قراءة السجل.");
      }).catch(() => { if (!stop) setText("تعذر قراءة السجل."); });
    };
    load();
    const timer = window.setInterval(load, 3000);
    return () => { stop = true; window.clearInterval(timer); };
  }, []);
  return (
    <main dir="rtl">
      <h1>myLog.txt</h1>
      <pre>{text}</pre>
    </main>
  );
}
