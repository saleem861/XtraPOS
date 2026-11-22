import { CalendarClock, CheckCheck, FileSpreadsheet, HandCoins, Printer, RefreshCw, Scale, StickyNote, Trash2, UserRound, X } from "lucide-react";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  ACTION_TEXT,
  createCashErrorClient,
  decisionText,
  isOpen,
  kindText,
  METHOD_TEXT,
  monthEnd,
  monthStart,
  nextMonth,
  selectionNet,
  signedMoney,
  STATUS_TEXT,
  toggleSelection,
  type CashErrorActionType,
  type CashErrorFilters,
  type CashErrorItem,
  type CashErrorList,
  type CashErrorStatement,
  type CashErrorSummary,
  type SettleMethod,
} from "../api/cashErrors";
import { usesDesktopCustomerKeyboard } from "../customers/newCustomerForm";
import { distinctBy } from "../distinct";
import type { CurrentUser, DeviceSession } from "../types/setup";
import { FullKeyboard } from "./FullKeyboard";
import { KeyboardToggle, useFullKeyboardVisible } from "./keyboardVisibility";
import { ScreenHeader } from "./ScreenHeader";
import { Waiting } from "./Waiting";
import "./employeeCashErrors.css";

function stamp(value: string) {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;
  return date.toLocaleString("ar-PS-u-nu-latn", { year: "numeric", month: "2-digit", day: "2-digit", hour: "2-digit", minute: "2-digit" });
}

function day(value: string) {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;
  return date.toLocaleDateString("ar-PS-u-nu-latn", { year: "numeric", month: "2-digit", day: "2-digit" });
}

function amountClass(value: number) {
  return value < 0 ? "is-short" : value > 0 ? "is-over" : "is-zero";
}

const emptyFilters = (): CashErrorFilters => ({
  from: monthStart(),
  to: monthEnd(),
  employeeId: "",
  branchId: "",
  stationId: "",
  type: "",
  status: "",
});

interface Dialog {
  type: CashErrorActionType;
}

export function EmployeeCashErrorsScreen({ session, user, onBack }: { session: DeviceSession; user: CurrentUser; onBack: () => void }) {
  const client = useMemo(() => createCashErrorClient(session), [session]);
  const desktop = usesDesktopCustomerKeyboard();
  const [keyboardOpen] = useFullKeyboardVisible();
  const [filters, setFilters] = useState<CashErrorFilters>(emptyFilters);
  const [data, setData] = useState<CashErrorList | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const [message, setMessage] = useState("");
  const [info, setInfo] = useState("");
  const [tab, setTab] = useState<"errors" | "employees">("errors");
  const [selected, setSelected] = useState<string[]>([]);
  const [dialog, setDialog] = useState<Dialog | null>(null);
  const [statementFor, setStatementFor] = useState<string | null>(null);
  const request = useRef(0);

  const load = useCallback(async (next: CashErrorFilters) => {
    const id = request.current + 1;
    request.current = id;
    setLoading(true);
    setError("");
    try {
      const list = await client.list(next);
      if (request.current !== id) return;
      setData(list);
      setSelected((current) => current.filter((itemId) => list.items.some((item) => item.itemId === itemId && isOpen(item))));
    } catch (caught) {
      if (request.current === id) setError(caught instanceof Error ? caught.message : "تعذر تحميل أخطاء الموظفين.");
    } finally {
      if (request.current === id) setLoading(false);
    }
  }, [client]);

  // التصفية تُطبَّق تلقائياً بعد لحظة من آخر تغيير.
  useEffect(() => {
    const timer = window.setTimeout(() => void load(filters), 300);
    return () => window.clearTimeout(timer);
  }, [filters, load]);

  const options = data?.options;
  // المشرف: فرعه وحده (بالاسم، لأن الفرع قد يظهر بأكثر من معرّف). القوائم مميزة دائماً.
  const branches = useMemo(() => {
    const list = distinctBy(options?.branches, (branch) => branch.name);
    if (!data || data.canManageAll) return list;
    return list.filter((branch) => branch.id === data.scopedBranchId || (!!data.scopedBranchName && branch.name === data.scopedBranchName));
  }, [options, data]);
  const stations = useMemo(() => {
    const allowed = new Set((filters.branchId ? branches.filter((branch) => branch.id === filters.branchId) : data?.canManageAll ? [] : branches).map((branch) => branch.id));
    return distinctBy(
      (options?.stations ?? []).filter((station) => allowed.size === 0 ? !filters.branchId : allowed.has(station.branchId)),
      (station) => `${station.stationNumber}|${station.name}`,
    );
  }, [options, filters.branchId, branches, data]);
  const users = useMemo(() => distinctBy(options?.users, (row) => `${row.id}`).filter((row, index, all) => all.findIndex((other) => other.name === row.name) === index), [options]);
  const items = data?.items ?? [];
  const net = selectionNet(selected, items);
  const selectedEmployee = items.find((item) => item.itemId === selected[0]);

  function change(patch: Partial<CashErrorFilters>) {
    setMessage("");
    setInfo("");
    setFilters((current) => ({ ...current, ...patch }));
  }

  function choose(item: CashErrorItem) {
    const result = toggleSelection(selected, item, items);
    setSelected(result.selected);
    setMessage(result.error);
    setInfo("");
  }

  async function exportExcel() {
    try {
      const file = await client.exportExcel(filters);
      const link = document.createElement("a");
      link.href = URL.createObjectURL(file.blob);
      link.download = file.fileName;
      link.click();
      URL.revokeObjectURL(link.href);
    } catch (caught) {
      setMessage(caught instanceof Error ? caught.message : "تعذر التصدير.");
    }
  }

  if (statementFor) {
    return (
      <EmployeeStatement
        session={session}
        user={user}
        employeeId={statementFor}
        from={filters.from}
        to={filters.to}
        onBack={() => { setStatementFor(null); void load(filters); }}
      />
    );
  }

  return (
    <section className={desktop && keyboardOpen ? "new-customer sales-review has-keyboard cash-errors" : "new-customer sales-review cash-errors"} dir="rtl" aria-label="أخطاء الموظفين">
      <ScreenHeader title="أخطاء الموظفين" context={user.branchName} onBack={onBack} />
      <div className="cash-errors-body">
        <div className="cash-errors-filters">
          <label>من<input type="date" value={filters.from} onChange={(event) => change({ from: event.target.value })} /></label>
          <label>إلى<input type="date" value={filters.to} onChange={(event) => change({ to: event.target.value })} /></label>
          <div className="cash-errors-presets">
            <button type="button" onClick={() => change({ from: monthStart(), to: monthEnd() })}>هذا الشهر</button>
            <button type="button" onClick={() => {
              const last = new Date();
              last.setDate(1);
              last.setMonth(last.getMonth() - 1);
              change({ from: monthStart(last), to: monthEnd(last) });
            }}>الشهر الماضي</button>
          </div>
          <label>الموظف
            <select value={filters.employeeId} onChange={(event) => change({ employeeId: event.target.value })}>
              <option value="">كل الموظفين</option>
              {users.map((row) => <option key={row.id} value={row.id}>{row.name}</option>)}
            </select>
          </label>
          <label>الفرع
            <select value={filters.branchId} disabled={!data?.canManageAll} onChange={(event) => change({ branchId: event.target.value, stationId: "" })}>
              <option value="">{data?.canManageAll ? "كل الفروع" : "فرعي"}</option>
              {branches.map((branch) => <option key={branch.id} value={branch.id}>{branch.name}</option>)}
            </select>
          </label>
          <label>النقطة
            <select value={filters.stationId} onChange={(event) => change({ stationId: event.target.value })}>
              <option value="">كل النقاط</option>
              {stations.map((station) => <option key={station.id} value={station.id}>{station.stationNumber} — {station.name}</option>)}
            </select>
          </label>
          <label>النوع
            <select value={filters.type} onChange={(event) => change({ type: event.target.value as CashErrorFilters["type"] })}>
              <option value="">نقص وزيادة</option>
              <option value="Shortage">نقص</option>
              <option value="Surplus">زيادة</option>
            </select>
          </label>
          <label>الحالة
            <select value={filters.status} onChange={(event) => change({ status: event.target.value as CashErrorFilters["status"] })}>
              <option value="">الكل</option>
              <option value="Unsettled">غير مسوّى (مع المؤجل)</option>
              <option value="Open">غير مسوّى</option>
              <option value="Deferred">مؤجل</option>
              <option value="Settled">مسوّى</option>
              <option value="Offset">مُقاص</option>
              <option value="Waived">معفى</option>
            </select>
          </label>
          <div className="cash-errors-tools">
            <button type="button" onClick={() => void load(filters)}><RefreshCw aria-hidden="true" />تحديث</button>
            <button type="button" className="is-danger" onClick={() => { setSelected([]); setFilters(emptyFilters()); }}><Trash2 aria-hidden="true" />إلغاء الفلاتر</button>
            <button type="button" className="is-excel" onClick={() => void exportExcel()}><FileSpreadsheet aria-hidden="true" />Excel</button>
          </div>
        </div>

        {data?.notice ? <p className="cash-errors-notice">{data.notice}</p> : null}
        {data ? <SummaryCards summary={data.summary} /> : null}

        <div className="cash-errors-tabs" role="tablist" aria-label="عرض الأخطاء">
          <button type="button" role="tab" aria-selected={tab === "errors"} className={tab === "errors" ? "is-selected" : undefined} onClick={() => setTab("errors")}>الأخطاء</button>
          <button type="button" role="tab" aria-selected={tab === "employees"} className={tab === "employees" ? "is-selected" : undefined} onClick={() => setTab("employees")}>أرصدة الموظفين</button>
        </div>

        <div className="cash-errors-results" style={{ position: "relative" }}>
          {error ? <p className="form-error">{error}</p> : null}
          {message ? <p className="form-error" role="alert">{message}</p> : null}
          {info ? <p className="update-result is-ok" role="status">{info}</p> : null}
          {loading && !data ? <Waiting label="جاري تحميل أخطاء الموظفين…" /> : null}
          {loading && data ? <Waiting variant="overlay" label="جاري التحديث…" /> : null}
          {data && tab === "errors" ? (
            <ErrorsTable items={items} selected={selected} onToggle={choose} onEmployee={setStatementFor} />
          ) : null}
          {data && tab === "employees" ? (
            <div className="cash-errors-balances">
              {data.employees.length === 0 ? <p className="cash-errors-empty">لا توجد أخطاء في هذه الفترة.</p> : data.employees.map((balance) => (
                <button key={balance.employeeId} type="button" className="cash-errors-balance" onClick={() => setStatementFor(balance.employeeId)}>
                  <span className="cash-errors-balance-name"><UserRound aria-hidden="true" />{balance.employeeName}</span>
                  <strong className={amountClass(balance.summary.openNet)}>{signedMoney(balance.summary.openNet)}</strong>
                  <small>الصافي المستحق — {balance.summary.openCount} غير مسوّى من {balance.summary.count}</small>
                  <span className="cash-errors-balance-split">
                    <em className="is-short">نقص {signedMoney(balance.summary.shortage)}</em>
                    <em className="is-over">زيادة {signedMoney(balance.summary.surplus)}</em>
                  </span>
                  <span className="cash-errors-balance-link">كشف الحساب ←</span>
                </button>
              ))}
            </div>
          ) : null}
        </div>
      </div>

      {selected.length > 0 ? (
        <div className="cash-errors-selection" role="region" aria-label="الأخطاء المختارة">
          <span><b>{selected.length}</b> مختار — {selectedEmployee?.employeeName}</span>
          <span>الصافي <strong className={amountClass(net)}>{signedMoney(net)}</strong></span>
          <div>
            <button type="button" disabled={selected.length < 2} title={selected.length < 2 ? "المقاصة تحتاج خطأين على الأقل" : undefined} onClick={() => setDialog({ type: "Offset" })}><Scale aria-hidden="true" />مقاصة</button>
            <button type="button" onClick={() => setDialog({ type: "Settle" })}><HandCoins aria-hidden="true" />تسوية</button>
            <button type="button" onClick={() => setDialog({ type: "Defer" })}><CalendarClock aria-hidden="true" />تأجيل</button>
            <button type="button" onClick={() => setDialog({ type: "Note" })}><StickyNote aria-hidden="true" />ملاحظة</button>
            <button type="button" className="is-plain" onClick={() => { setSelected([]); setMessage(""); }}><X aria-hidden="true" />إلغاء الاختيار</button>
          </div>
        </div>
      ) : null}

      {dialog && selectedEmployee ? (
        <ActionDialog
          type={dialog.type}
          count={selected.length}
          net={net}
          employeeName={selectedEmployee.employeeName}
          onCancel={() => setDialog(null)}
          onSubmit={async (extra) => {
            const result = await client.act({ type: dialog.type, employeeId: selectedEmployee.employeeId, itemIds: selected, ...extra });
            setDialog(null);
            setSelected([]);
            setMessage("");
            await load(filters);
            return result.message;
          }}
          onDone={(text) => setInfo(text)}
        />
      ) : null}

      {desktop ? <KeyboardToggle /> : null}
      {desktop && keyboardOpen ? <FullKeyboard onEnter={() => void load(filters)} /> : null}
    </section>
  );
}

function SummaryCards({ summary }: { summary: CashErrorSummary }) {
  return (
    <div className="cash-errors-cards">
      <article className="is-short"><span>النقص</span><strong>{signedMoney(summary.shortage)}</strong></article>
      <article className="is-over"><span>الزيادة</span><strong>{signedMoney(summary.surplus)}</strong></article>
      <article><span>المعفى</span><strong>{signedMoney(summary.waived)}</strong></article>
      <article><span>المسوّى</span><strong>{signedMoney(summary.settled)}</strong></article>
      <article className={`is-net ${amountClass(summary.openNet)}`}><span>الصافي المستحق</span><strong>{signedMoney(summary.openNet)}</strong></article>
      <article><span>الأخطاء</span><strong>{summary.count}</strong><small>{summary.openCount} غير مسوّى</small></article>
    </div>
  );
}

function ErrorsTable({ items, selected, onToggle, onEmployee, readOnly = false }: {
  items: CashErrorItem[];
  selected: string[];
  onToggle: (item: CashErrorItem) => void;
  onEmployee?: (employeeId: string) => void;
  readOnly?: boolean;
}) {
  return (
    <div className="cash-errors-table">
      <table>
        <thead>
          <tr>
            {readOnly ? null : <th aria-label="اختيار" />}
            <th>التاريخ</th>
            <th>الموظف</th>
            <th>الفرع</th>
            <th>النقطة</th>
            <th>المبلغ</th>
            <th>النوع</th>
            <th>المبرر</th>
            <th>القرار</th>
            <th>الحالة</th>
          </tr>
        </thead>
        <tbody>
          {items.length === 0 ? <tr><td colSpan={readOnly ? 9 : 10} className="cash-errors-empty">لا توجد أخطاء مطابقة.</td></tr> : items.map((item) => {
            const chosen = selected.includes(item.itemId);
            return (
              <tr key={item.itemId} className={chosen ? "is-chosen" : isOpen(item) ? undefined : "is-closed"} onClick={readOnly ? undefined : () => onToggle(item)}>
                {readOnly ? null : (
                  <td>
                    <input
                      type="checkbox"
                      aria-label={`اختيار خطأ ${item.employeeName}`}
                      checked={chosen}
                      disabled={!isOpen(item)}
                      onClick={(event) => event.stopPropagation()}
                      onChange={() => onToggle(item)}
                    />
                  </td>
                )}
                <td>
                  {day(item.effectiveAt)}
                  {item.deferredTo ? <small className="cash-errors-sub">الأصل {day(item.occurredAt)}</small> : null}
                </td>
                <td>
                  {onEmployee ? (
                    <button type="button" className="cash-errors-link" onClick={(event) => { event.stopPropagation(); onEmployee(item.employeeId); }}>{item.employeeName}</button>
                  ) : item.employeeName}
                </td>
                <td>{item.branchName ?? "—"}</td>
                <td>{item.stationNumber != null ? `${item.stationNumber} ${item.stationName ?? ""}` : "—"}</td>
                <td className={`cash-errors-amount ${amountClass(item.amount)}`}>{signedMoney(item.amount)}</td>
                <td>{kindText(item)}</td>
                <td className="cash-errors-reason">{item.justification || "—"}{item.noteCount > 0 ? <small className="cash-errors-sub">{item.noteCount} ملاحظة</small> : null}</td>
                <td>{decisionText(item.decisionCode)}</td>
                <td>
                  <span className={`cash-errors-status is-${item.status.toLowerCase()}`}>{STATUS_TEXT[item.status]}</span>
                  {item.closedByName ? <small className="cash-errors-sub">{item.closedMethod ? METHOD_TEXT[item.closedMethod as SettleMethod] ?? "" : ""} {item.closedByName}</small> : null}
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}

function ActionDialog({ type, count, net, employeeName, onCancel, onSubmit, onDone }: {
  type: CashErrorActionType;
  count: number;
  net: number;
  employeeName: string;
  onCancel: () => void;
  onSubmit: (extra: { method?: SettleMethod; deferTo?: string; note?: string }) => Promise<string>;
  onDone: (message: string) => void;
}) {
  const [method, setMethod] = useState<SettleMethod>("Payroll");
  const [month, setMonth] = useState(nextMonth());
  const [note, setNote] = useState("");
  const [pending, setPending] = useState(false);
  const [error, setError] = useState("");
  const title = {
    Offset: "مقاصة الأخطاء المختارة",
    Settle: "تسوية الأخطاء المختارة",
    Defer: "تأجيل الأخطاء المختارة",
    Note: "إضافة ملاحظة",
  }[type];

  async function submit() {
    if (type === "Note" && !note.trim()) {
      setError("اكتب الملاحظة.");
      return;
    }
    setPending(true);
    setError("");
    try {
      const text = await onSubmit({
        method: type === "Settle" ? method : undefined,
        deferTo: type === "Defer" ? `${month}-01` : undefined,
        note: note.trim() || undefined,
      });
      onDone(text);
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : "تعذر حفظ الإجراء.");
      setPending(false);
    }
  }

  return (
    <div className="cash-errors-dialog-backdrop" role="presentation">
      <div className="cash-errors-dialog" role="dialog" aria-modal="true" aria-label={title} style={{ position: "relative" }}>
        <h3>{title}</h3>
        <p className="cash-errors-dialog-who">{employeeName} — {count} {count === 1 ? "خطأ" : "أخطاء"}</p>
        {type === "Offset" ? (
          <p className="cash-errors-dialog-explain">
            تُغلق الأخطاء المختارة، و{net === 0 ? "لا يبقى على الموظف شيء منها." : <>يبقى على الموظف رصيد واحد بقيمة <strong className={amountClass(net)}>{signedMoney(net)}</strong>.</>}
          </p>
        ) : null}
        {type === "Settle" ? (
          <>
            <p className="cash-errors-dialog-explain">المبلغ المسوّى <strong className={amountClass(net)}>{signedMoney(net)}</strong></p>
            <fieldset className="cash-errors-methods">
              <legend>طريقة التسوية</legend>
              {(Object.keys(METHOD_TEXT) as SettleMethod[]).map((key) => (
                <label key={key} className={method === key ? "is-selected" : undefined}>
                  <input type="radio" name="settle-method" value={key} checked={method === key} onChange={() => setMethod(key)} />
                  {METHOD_TEXT[key]}
                </label>
              ))}
            </fieldset>
          </>
        ) : null}
        {type === "Defer" ? (
          <label className="cash-errors-field">التأجيل إلى شهر<input type="month" value={month} onChange={(event) => setMonth(event.target.value)} /></label>
        ) : null}
        <label className="cash-errors-field">{type === "Note" ? "الملاحظة" : "ملاحظة (اختيارية)"}
          <textarea rows={3} value={note} maxLength={500} onChange={(event) => setNote(event.target.value)} autoFocus={type === "Note"} />
        </label>
        <p className="cash-errors-dialog-hint">الإجراء سجل جديد لا يُعدَّل ولا يُحذف، ويبقى الخطأ الأصلي كما هو.</p>
        {error ? <p className="form-error">{error}</p> : null}
        <div className="cash-errors-dialog-actions">
          <button type="button" className="primary-button" disabled={pending} onClick={() => void submit()}><CheckCheck aria-hidden="true" />تأكيد {ACTION_TEXT[type]}</button>
          <button type="button" className="ghost-button" disabled={pending} onClick={onCancel}>رجوع</button>
        </div>
        {pending ? <Waiting variant="overlay" label="جاري الحفظ…" /> : null}
      </div>
    </div>
  );
}

function EmployeeStatement({ session, user, employeeId, from, to, onBack }: {
  session: DeviceSession;
  user: CurrentUser;
  employeeId: string;
  from: string;
  to: string;
  onBack: () => void;
}) {
  const client = useMemo(() => createCashErrorClient(session), [session]);
  const [statement, setStatement] = useState<CashErrorStatement | null>(null);
  const [error, setError] = useState("");

  useEffect(() => {
    let stop = false;
    void client.statement(employeeId, from, to)
      .then((result) => { if (!stop) setStatement(result); })
      .catch((caught: unknown) => { if (!stop) setError(caught instanceof Error ? caught.message : "تعذر تحميل الكشف."); });
    return () => { stop = true; };
  }, [client, employeeId, from, to]);

  return (
    <section className="new-customer sales-review cash-errors" dir="rtl" aria-label="كشف أخطاء الموظف">
      <ScreenHeader title="كشف أخطاء الموظف" context={user.branchName} onBack={onBack} />
      <div className="cash-errors-body">
        {error ? <p className="form-error">{error}</p> : null}
        {!statement && !error ? <Waiting label="جاري تحميل كشف الموظف…" /> : null}
        {statement ? (
          <>
            <div className="cash-errors-statement-head">
              <div>
                <h2><UserRound aria-hidden="true" />{statement.employee.name}</h2>
                <p>الفترة {day(statement.from)} — {day(statement.to)}</p>
              </div>
              <div className="cash-errors-tools">
                <button type="button" onClick={() => printStatement(statement, user, "receipt")}><Printer aria-hidden="true" />طباعة وصل</button>
                <button type="button" onClick={() => printStatement(statement, user, "a4")}><Printer aria-hidden="true" />طباعة A4</button>
              </div>
            </div>
            {statement.notice ? <p className="cash-errors-notice">{statement.notice}</p> : null}
            <SummaryCards summary={statement.summary} />
            <h3 className="cash-errors-section">الأخطاء</h3>
            <ErrorsTable items={statement.items} selected={[]} onToggle={() => undefined} readOnly />
            <h3 className="cash-errors-section">الإجراءات</h3>
            {statement.actions.length === 0 ? <p className="cash-errors-empty">لا توجد إجراءات في هذه الفترة.</p> : (
              <ol className="cash-errors-timeline">
                {statement.actions.map((action) => (
                  <li key={action.actionId} className={`is-${action.type.toLowerCase()}`}>
                    <strong>{ACTION_TEXT[action.type]}</strong>
                    <span>{stamp(action.createdAt)} — {action.actorName ?? ""}</span>
                    <span>
                      {action.items.length} {action.items.length === 1 ? "خطأ" : "أخطاء"}
                      {action.type === "Offset" ? <> — المتبقي <b className={amountClass(action.amount)}>{signedMoney(action.amount)}</b></> : null}
                      {action.type === "Settle" ? <> — <b className={amountClass(action.amount)}>{signedMoney(action.amount)}</b> {action.method ? METHOD_TEXT[action.method] : ""}</> : null}
                      {action.type === "Defer" && action.deferTo ? <> — إلى {action.deferTo.slice(0, 7)}</> : null}
                    </span>
                    {action.note ? <p>{action.note}</p> : null}
                  </li>
                ))}
              </ol>
            )}
          </>
        ) : null}
      </div>
    </section>
  );
}

const HTML_ESCAPES: Record<string, string> = { "&": "&amp;", "<": "&lt;", ">": "&gt;", "\"": "&quot;", "'": "&#39;" };

function escapeHtml(value: string) {
  return value.replace(/[&<>"']/g, (char) => HTML_ESCAPES[char] ?? char);
}

/** طباعة الكشف: وصل حراري 80 ملم أو صفحة A4، عبر إطار مخفي. */
function printStatement(statement: CashErrorStatement, user: CurrentUser, layout: "receipt" | "a4") {
  const rows = statement.items.map((item) => `
    <tr><td>${escapeHtml(day(item.effectiveAt))}</td><td>${escapeHtml(kindText(item))}</td>${layout === "a4" ? `<td>${escapeHtml(item.branchName ?? "")} ${item.stationNumber ?? ""}</td><td>${escapeHtml(item.justification ?? "")}</td>` : ""}<td>${escapeHtml(STATUS_TEXT[item.status])}</td><td class="num">${signedMoney(item.amount)}</td></tr>`).join("");
  const summary = statement.summary;
  const html = `<!DOCTYPE html><html lang="ar" dir="rtl"><head><meta charset="utf-8"><title>كشف أخطاء ${escapeHtml(statement.employee.name)}</title>
<style>
  @page { size: ${layout === "receipt" ? "80mm auto" : "A4"}; margin: ${layout === "receipt" ? "4mm" : "14mm"}; }
  body { font-family: Tahoma, Arial, sans-serif; color: #111; font-size: ${layout === "receipt" ? "11px" : "12px"}; }
  h1 { font-size: ${layout === "receipt" ? "14px" : "18px"}; margin: 0 0 4px; text-align: center; }
  p { margin: 2px 0; text-align: center; }
  table { width: 100%; border-collapse: collapse; margin-top: 8px; }
  th, td { border-bottom: 1px solid #999; padding: 3px 2px; text-align: right; }
  .num { direction: ltr; text-align: left; white-space: nowrap; }
  .totals td { font-weight: bold; border-bottom: 0; }
  .sign { margin-top: 24px; display: flex; justify-content: space-between; }
</style></head><body>
  <h1>${escapeHtml(user.businessDisplayName || user.businessName)}</h1>
  <p>كشف أخطاء الجرار — ${escapeHtml(statement.employee.name)}</p>
  <p>${escapeHtml(day(statement.from))} — ${escapeHtml(day(statement.to))}</p>
  <table><thead><tr><th>التاريخ</th><th>النوع</th>${layout === "a4" ? "<th>الفرع/النقطة</th><th>المبرر</th>" : ""}<th>الحالة</th><th class="num">المبلغ</th></tr></thead><tbody>${rows}</tbody></table>
  <table class="totals"><tbody>
    <tr><td>النقص</td><td class="num">${signedMoney(summary.shortage)}</td></tr>
    <tr><td>الزيادة</td><td class="num">${signedMoney(summary.surplus)}</td></tr>
    <tr><td>المعفى</td><td class="num">${signedMoney(summary.waived)}</td></tr>
    <tr><td>الصافي المستحق</td><td class="num">${signedMoney(summary.openNet)}</td></tr>
  </tbody></table>
  <div class="sign"><span>توقيع الموظف: ............</span><span>توقيع المشرف: ............</span></div>
  <p>طُبع ${escapeHtml(stamp(new Date().toISOString()))} بواسطة ${escapeHtml(user.displayName)}</p>
</body></html>`;
  const frame = document.createElement("iframe");
  frame.setAttribute("aria-hidden", "true");
  frame.style.position = "fixed";
  frame.style.width = "0";
  frame.style.height = "0";
  frame.style.border = "0";
  document.body.appendChild(frame);
  const doc = frame.contentDocument;
  if (!doc || !frame.contentWindow) {
    frame.remove();
    return;
  }
  doc.open();
  doc.write(html);
  doc.close();
  window.setTimeout(() => {
    frame.contentWindow?.focus();
    frame.contentWindow?.print();
    window.setTimeout(() => frame.remove(), 1000);
  }, 100);
}
