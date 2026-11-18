import { ChevronDown, RefreshCw, Trash2 } from "lucide-react";
import { useEffect, useState, type KeyboardEvent } from "react";
import { deviceHeaders } from "../api/client";
import { usesDesktopCustomerKeyboard } from "../customers/newCustomerForm";
import type { DeviceSession } from "../types/setup";
import { FullKeyboard } from "./FullKeyboard";
import { ScreenHeader } from "./ScreenHeader";

interface ReviewLine {
  countId: string;
  countedAt: string;
  cashier: string;
  branchName: string;
  stationNumber: number;
  stationName: string;
  attempt: number;
  countedAmount: number;
  difference: number;
  status: "Matched" | "Shortage" | "Surplus";
  justification: string | null;
  final: boolean;
  decision: string | null;
}

interface DetailLine {
  displayName: string;
  unitValue: number;
  quantity: number;
  lineAmount: number;
}

interface CountDetail {
  countId: string;
  countedAt: string;
  cashier: string;
  branchName: string;
  stationNumber: number;
  stationName: string;
  attempt: number;
  countedAmount: number;
  difference: number;
  status: ReviewLine["status"];
  justification: string | null;
  final: boolean;
  decision: string | null;
  decisionNote: string | null;
  supervisorName: string | null;
  decidedAt: string | null;
  lines: DetailLine[];
}

interface Options {
  branches: { id: string; name: string }[];
  stations: { id: string; branchId: string; stationNumber: number; name: string }[];
  users: { id: string; name: string }[];
}

export function CashDrawerReviewScreen({ session, onBack }: { session: DeviceSession; onBack: () => void }) {
  const [lines, setLines] = useState<ReviewLine[]>([]);
  const [total, setTotal] = useState(0);
  const [shortage, setShortage] = useState(0);
  const [options, setOptions] = useState<Options>({ branches: [], stations: [], users: [] });
  const [page, setPage] = useState(0);
  const [branchId, setBranchId] = useState("");
  const [stationId, setStationId] = useState("");
  const [userId, setUserId] = useState("");
  const [dateFrom, setDateFrom] = useState("");
  const [dateTo, setDateTo] = useState("");
  const [status, setStatus] = useState("");
  const [decision, setDecision] = useState("");
  const [detail, setDetail] = useState<{ count: CountDetail; firstAttempt: CountDetail | null } | null>(null);
  const [note, setNote] = useState("");
  const [message, setMessage] = useState("");
  const desktop = usesDesktopCustomerKeyboard() || (typeof window.matchMedia === "function" && window.matchMedia("(orientation: landscape) and (min-width: 900px)").matches);
  const [keyboardOpen, setKeyboardOpen] = useState(false);
  const stations = options.stations.filter((station) => !branchId || station.branchId === branchId);

  async function load(nextPage = page) {
    const params = new URLSearchParams({ skip: String(nextPage * 50), take: "50" });
    if (branchId) params.set("branchId", branchId);
    if (stationId) params.set("stationId", stationId);
    if (userId) params.set("userId", userId);
    if (dateFrom) params.set("dateFrom", dateFrom);
    if (dateTo) params.set("dateTo", dateTo);
    if (status) params.set("status", status);
    if (decision) params.set("decision", decision);
    const response = await fetch(`/api/cash-drawer/reviews?${params}`, { headers: deviceHeaders(session) });
    if (!response.ok) {
      setMessage("تعذر قراءة عمليات الجرد.");
      return;
    }
    const payload = await response.json() as { total: number; shortageTotal: number; lines: ReviewLine[]; options: Options };
    setLines(payload.lines);
    setTotal(payload.total);
    setShortage(payload.shortageTotal);
    setOptions(payload.options);
    setMessage("");
  }

  useEffect(() => { void load(0); }, [session]);

  function apply() {
    setPage(0);
    void load(0);
  }

  function move(event: KeyboardEvent<HTMLElement>) {
    if (event.key !== "Enter") return;
    const fields = [...document.querySelectorAll<HTMLElement>("[data-review-field]")].filter((field) => !field.hasAttribute("disabled"));
    const index = fields.indexOf(event.target as HTMLElement);
    if (index < 0) return;
    event.preventDefault();
    if (index === fields.length - 1) apply();
    else fields[index + 1]?.focus();
  }

  async function open(countId: string) {
    const response = await fetch(`/api/cash-drawer/reviews/${countId}`, { headers: deviceHeaders(session) });
    if (!response.ok) return;
    setNote("");
    setDetail(await response.json());
  }

  async function decide(code: string) {
    if (!detail || note.trim().length === 0) {
      setMessage("ملاحظة القرار مطلوبة.");
      return;
    }
    const response = await fetch(`/api/cash-drawer/reviews/${detail.count.countId}/decision`, {
      method: "POST",
      headers: { ...deviceHeaders(session), "Content-Type": "application/json" },
      body: JSON.stringify({ decisionCode: code, note }),
    });
    const payload = await response.json().catch(() => null) as { message?: string } | null;
    if (!response.ok) {
      setMessage(payload?.message || "تعذر حفظ القرار.");
      return;
    }
    setMessage("");
    await open(detail.count.countId);
    void load();
  }

  const count = detail?.count;
  const canDecide = count?.final && count.status !== "Matched" && !count.decision;
  return (
    <section className={desktop && keyboardOpen ? "new-customer sales-review has-keyboard" : "new-customer sales-review"} dir="rtl" aria-label="مراجعة الجرد" onKeyDown={move}>
      <ScreenHeader title="مراجعة الجرد" onBack={detail ? () => setDetail(null) : onBack} />
      <div className="sales-review-body">
        <aside className="sales-review-actions">
          <h3>إجراءات سريعة</h3>
          <button type="button" onClick={() => void load()}><RefreshCw aria-hidden="true" />تحديث</button>
          <button type="button" className="is-danger" onClick={() => { setBranchId(""); setStationId(""); setUserId(""); setDateFrom(""); setDateTo(""); setStatus(""); setDecision(""); setPage(0); }}><Trash2 aria-hidden="true" />إلغاء الفلاتر</button>
          <article className="is-sales"><div><span>عدد عمليات الجرد</span><strong>{total}</strong></div></article>
          <article className="is-profit"><div><span>مجموع النقص المحسوم</span><strong>₪ {money(shortage)}</strong></div></article>
        </aside>
        <div className="sales-review-main">
          <div className="sales-review-filters drawer-filters">
            <label>من تاريخ<input data-review-field type="date" value={dateFrom} onChange={(event) => setDateFrom(event.target.value)} /></label>
            <label>إلى تاريخ<input data-review-field type="date" value={dateTo} onChange={(event) => setDateTo(event.target.value)} /></label>
            <label>الموظف<select data-review-field value={userId} onChange={(event) => setUserId(event.target.value)}><option value="">الكل</option>{options.users.map((user) => <option key={user.id} value={user.id}>{user.name}</option>)}</select></label>
            <label>الفرع<select data-review-field value={branchId} onChange={(event) => { setBranchId(event.target.value); setStationId(""); }}><option value="">كل الفروع</option>{options.branches.map((branch) => <option key={branch.id} value={branch.id}>{branch.name}</option>)}</select></label>
            <label>النقطة<select data-review-field value={stationId} onChange={(event) => setStationId(event.target.value)}><option value="">كل النقاط</option>{stations.map((station) => <option key={station.id} value={station.id}>{station.stationNumber} — {station.name}</option>)}</select></label>
            <label>الحالة<select data-review-field value={status} onChange={(event) => setStatus(event.target.value)}><option value="">الكل</option><option value="Matched">مطابق</option><option value="Shortage">نقص</option><option value="Surplus">زيادة</option></select></label>
            <label>القرار<select data-review-field value={decision} onChange={(event) => setDecision(event.target.value)}><option value="">الكل</option><option value="None">بلا قرار</option><option value="KeepShortage">إبقاء النقص</option><option value="KeepSurplus">اعتماد الزيادة</option><option value="Waive">إعفاء</option></select></label>
          </div>
        </div>
        <div className="sales-review-results">
          {message ? <p className="form-error">{message}</p> : null}
          <div className="sales-review-table">
            <table>
              <thead><tr><th>عرض</th><th>الوقت</th><th>الموظف</th><th>الفرع</th><th>النقطة</th><th>المحاولة</th><th>المعدود</th><th>الفارق</th><th>الحالة</th><th>التبرير</th><th>القرار</th></tr></thead>
              <tbody>
                {lines.length === 0 ? <tr><td colSpan={11}>لا توجد عمليات جرد.</td></tr> : lines.map((line) => (
                  <tr key={line.countId}>
                    <td><button type="button" className="sales-review-view" onClick={() => void open(line.countId)}>عرض</button></td>
                    <td>{stamp(line.countedAt)}</td>
                    <td>{line.cashier}</td>
                    <td>{line.branchName}</td>
                    <td>{line.stationNumber}</td>
                    <td>{line.attempt === 1 ? "الأول" : "الثاني"}</td>
                    <td>{money(line.countedAmount)}</td>
                    <td className={line.status === "Shortage" ? "is-short" : line.status === "Surplus" ? "is-over" : "is-match"}>{diffText(line.status, line.difference)}</td>
                    <td>{statusText(line.status)}</td>
                    <td>{line.justification || "—"}</td>
                    <td>{decisionText(line.decision)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <div className="sales-review-pager">
            <span>الصفحة {page + 1} — {total} عملية</span>
            <button type="button" disabled={page <= 0} onClick={() => { setPage(page - 1); void load(page - 1); }}>السابق</button>
            <button type="button" disabled={(page + 1) * 50 >= total} onClick={() => { setPage(page + 1); void load(page + 1); }}>التالي</button>
          </div>
        </div>
      </div>
      {desktop ? (
        <button type="button" className="sales-review-keyboard-toggle" onClick={() => setKeyboardOpen((value) => !value)}>
          <ChevronDown aria-hidden="true" style={{ transform: keyboardOpen ? "none" : "rotate(180deg)" }} />
          {keyboardOpen ? "إخفاء لوحة المفاتيح والأرقام" : "إظهار لوحة المفاتيح والأرقام"}
        </button>
      ) : null}
      {desktop && keyboardOpen ? <FullKeyboard onEnter={apply} /> : null}
      {detail && count ? (
        <div className="sales-review-details drawer-detail">
          <div>
            <h3>الجرد المعتمد</h3>
            <CountFacts count={count} />
            {detail.firstAttempt ? <><h3>العد الأول</h3><CountFacts count={detail.firstAttempt} /></> : null}
            {canDecide ? (
              <div className="drawer-decision" onKeyDown={(event) => {
                if (event.key !== "Enter") return;
                const fields = [...event.currentTarget.querySelectorAll<HTMLElement>("[data-decision-field]")];
                const index = fields.indexOf(event.target as HTMLElement);
                if (index < 0 || index === fields.length - 1) return;
                event.preventDefault();
                event.stopPropagation();
                fields[index + 1]?.focus();
              }}>
                <label>ملاحظة<textarea data-decision-field value={note} onChange={(event) => setNote(event.target.value)} rows={3} /></label>
                <button data-decision-field type="button" onClick={() => void decide(count.status === "Shortage" ? "KeepShortage" : "KeepSurplus")}>{count.status === "Shortage" ? "إبقاء النقص" : "إبقاء الزيادة"}</button>
                {count.status === "Shortage" ? <button data-decision-field type="button" onClick={() => void decide("Waive")}>إعفاء الموظف</button> : null}
              </div>
            ) : count.decision ? (
              <p>القرار: {decisionText(count.decision)} — {count.supervisorName} — {count.decidedAt ? stamp(count.decidedAt) : ""} — {count.decisionNote}</p>
            ) : null}
          </div>
        </div>
      ) : null}
    </section>
  );
}

function CountFacts({ count }: { count: CountDetail }) {
  return (
    <div>
      <p>{stamp(count.countedAt)} — {count.cashier} — {count.branchName} — نقطة {count.stationNumber} {count.stationName} — المحاولة {count.attempt === 1 ? "الأولى" : "الثانية"}</p>
      <div className="drawer-grid">
        {count.lines.map((line) => (
          <article key={line.displayName + line.unitValue}><strong>{line.displayName}</strong><span>الكمية {line.quantity}</span><span>₪ {money(line.lineAmount)}</span></article>
        ))}
      </div>
      <p>المجموع المعدود ₪ {money(count.countedAmount)} — {diffText(count.status, count.difference)}</p>
      <p>{count.justification ? `التبرير: ${count.justification}` : "لا يوجد تبرير"}</p>
      <p>{count.status === "Shortage" ? "نقص محسوب على الموظف" : count.status === "Surplus" ? "زيادة مسجّلة" : "مطابق"}</p>
    </div>
  );
}

function statusText(status: ReviewLine["status"]) {
  if (status === "Matched") return "مطابق";
  if (status === "Shortage") return "نقص";
  return "زيادة";
}

function decisionText(decision: string | null) {
  if (decision === "KeepShortage") return "إبقاء النقص";
  if (decision === "KeepSurplus") return "اعتماد الزيادة";
  if (decision === "Waive") return "إعفاء";
  return "بلا قرار";
}

function diffText(status: ReviewLine["status"], difference: number) {
  if (status === "Matched") return "مطابق";
  const amount = money(Math.abs(difference));
  return status === "Shortage" ? `نقص ${amount}` : `زيادة ${amount}`;
}

function money(value: number) {
  return Number(value).toLocaleString("en-US", { minimumFractionDigits: 2, maximumFractionDigits: 2 });
}

function stamp(value: string) {
  return new Date(value).toLocaleString("en-GB", { timeZone: "Asia/Hebron", hour12: false });
}
