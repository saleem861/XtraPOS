export function traceUi(area: string, action: string, detail = "") {
  const body = JSON.stringify({ area, action, detail: detail.slice(0, 300) });
  if (navigator.sendBeacon) {
    navigator.sendBeacon("/api/diagnostics/events", new Blob([body], { type: "application/json" }));
    return;
  }
  void fetch("/api/diagnostics/events", { method: "POST", credentials: "same-origin", headers: { "Content-Type": "application/json" }, body, keepalive: true }).catch(() => undefined);
}
