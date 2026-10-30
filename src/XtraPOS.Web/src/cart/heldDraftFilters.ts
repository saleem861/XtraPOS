const zone = "Asia/Hebron";

export function hebronToday(now = new Date()): string {
  return new Intl.DateTimeFormat("en-CA", { timeZone: zone, year: "numeric", month: "2-digit", day: "2-digit" }).format(now);
}

export function shiftDate(iso: string, days: number): string {
  const [year, month, day] = iso.split("-").map(Number);
  const next = new Date(Date.UTC(year, month - 1, day + days));
  return next.toISOString().slice(0, 10);
}

export function draftRange(preset: "today" | "yesterday" | "week" | "month", today = hebronToday()): { from: string; to: string } {
  if (preset === "yesterday") {
    const day = shiftDate(today, -1);
    return { from: day, to: day };
  }
  if (preset === "week") return { from: shiftDate(today, -6), to: today };
  if (preset === "month") return { from: shiftDate(today, -29), to: today };
  return { from: today, to: today };
}

export function dateRangeError(from: string, to: string): string | null {
  if (!from || !to) return "حدد تاريخ البداية والنهاية.";
  if (to < from) return "تاريخ النهاية أسبق من تاريخ البداية.";
  return null;
}
