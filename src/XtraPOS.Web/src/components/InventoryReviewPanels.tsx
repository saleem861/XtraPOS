import { useCallback, useEffect, useMemo, useState, type FormEvent } from "react";
import type { InventoryType } from "../api/inventory";
import {
  newRequestId,
  type AuditLine,
  type AuditMode,
  type AuditView,
  type InventoryReport,
  type InventoryReviewClient,
  type ItemProcessing,
  type ReportBranch,
  type ReportFilter,
  type SessionDetail,
} from "../api/inventoryReview";
import {
  defaultReportRange,
  filterReportLines,
  formatDateTime,
  formatDifference,
  formatQuantity,
  INVENTORY_TYPES,
  normalizeDigits,
  parseCountInput,
  parseOptionalInteger,
  sessionStatusLabel,
  type ReportLineTab,
} from "../inventory/inventoryRules";
import { FullKeyboard } from "./FullKeyboard";
import { Pager, usePaging } from "./Pager";
import { KeyboardToggle, useFullKeyboardVisible } from "./keyboardVisibility";
import { usesDesktopCustomerKeyboard } from "../customers/newCustomerForm";
import { Waiting } from "./Waiting";
import { distinctBy } from "../distinct";

export interface PanelConfirmation {
  title: string;
  message: string;
  accept: string;
  onAccept: () => void;
}

/** «رجوع» في رأس الشاشة يسأل اللوحة أولاً: true = عادت خطوة داخلها. */
export type BackHandler = () => boolean;

/** مرجع ثابت لقائمة فارغة، فلا تتغير الصفحات بلا سبب. */
const EMPTY: never[] = [];

function DesktopKeyboard() {
  const desktop = usesDesktopCustomerKeyboard();
  const [open] = useFullKeyboardVisible();
  if (!desktop) return null;
  return (
    <>
      <KeyboardToggle />
      {open ? <FullKeyboard onEnter={() => (document.activeElement as HTMLElement | null)?.dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", bubbles: true }))} /> : null}
    </>
  );
}

// ======================================================================
// تقرير الجرد وتدقيق المشرف
// ======================================================================

type ReportView = { kind: "list" } | { kind: "session"; sessionId: string; branchId: string } | { kind: "audit"; auditId: string; sessionId: string };

export function InventoryReportPanel({ client, onMessage, onBusy, onConfirm, onRegisterBack }: {
  client: InventoryReviewClient;
  onMessage: (message: string) => void;
  onBusy: (label: string) => void;
  onConfirm: (confirmation: PanelConfirmation) => void;
  onRegisterBack: (handler: BackHandler | null) => void;
}) {
  const [view, setView] = useState<ReportView>({ kind: "list" });
  const [filter, setFilter] = useState<ReportFilter>(() => ({ branchId: "", ...defaultReportRange(new Date()), type: "", userId: "", status: "" }));
  const [report, setReport] = useState<InventoryReport | null>(null);
  const [loading, setLoading] = useState(false);
  const [branches, setBranches] = useState<ReportBranch[]>([]);
  const sessionsPaging = usePaging(report?.sessions ?? EMPTY, `${filter.branchId}|${filter.type}|${filter.userId}|${filter.status}|${filter.from}|${filter.to}`);

  useEffect(() => {
    let cancelled = false;
    void client.branches()
      .then((value) => { if (!cancelled) setBranches(distinctBy(value.branches, (branch) => branch.branchName)); })
      .catch(() => { if (!cancelled) setBranches([]); });
    return () => { cancelled = true; };
  }, [client]);

  const selectedBranch = branches.find((branch) => branch.branchId === filter.branchId);

  const load = useCallback(async (next: ReportFilter) => {
    setLoading(true);
    onMessage("");
    try {
      setReport(await client.report(next));
    } catch (error) {
      onMessage((error as Error).message);
    } finally {
      setLoading(false);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [client]);

  useEffect(() => { void load(filter); }, [load]); // eslint-disable-line react-hooks/exhaustive-deps

  useEffect(() => {
    onRegisterBack(() => {
      if (view.kind === "audit") { setView({ kind: "session", sessionId: view.sessionId, branchId: "" }); return true; }
      if (view.kind === "session") { setView({ kind: "list" }); void load(filter); return true; }
      return false;
    });
    return () => onRegisterBack(null);
  }, [view, filter, load, onRegisterBack]);

  if (view.kind === "session") {
    return (
      <SessionDetailView
        client={client}
        sessionId={view.sessionId}
        branchId={view.branchId}
        onMessage={onMessage}
        onBusy={onBusy}
        onOpenAudit={(auditId) => setView({ kind: "audit", auditId, sessionId: view.sessionId })}
      />
    );
  }

  if (view.kind === "audit") {
    return (
      <AuditPanel
        client={client}
        auditId={view.auditId}
        onMessage={onMessage}
        onBusy={onBusy}
        onConfirm={onConfirm}
      />
    );
  }

  return (
    <div className="inventory-report">
      <form
        className="inventory-filter"
        onSubmit={(event) => { event.preventDefault(); void load(filter); }}
      >
        {branches.length > 1 ? (
          <label>الفرع
            <select value={filter.branchId} onChange={(event) => setFilter({ ...filter, branchId: event.target.value, userId: "" })}>
              {branches.map((branch) => (
                <option key={branch.branchId} value={branch.current ? "" : branch.branchId}>
                  {branch.branchName}{branch.current ? " (هذا الفرع)" : ""}
                </option>
              ))}
            </select>
          </label>
        ) : null}
        <label>من تاريخ<input type="date" value={filter.from} onChange={(event) => setFilter({ ...filter, from: event.target.value })} /></label>
        <label>إلى تاريخ<input type="date" value={filter.to} onChange={(event) => setFilter({ ...filter, to: event.target.value })} /></label>
        <label>النوع
          <select value={filter.type} onChange={(event) => setFilter({ ...filter, type: event.target.value as InventoryType | "" })}>
            <option value="">الكل</option>
            {INVENTORY_TYPES.map((option) => <option key={option.value} value={option.value}>{option.label}</option>)}
          </select>
        </label>
        <label>الموظف
          <select value={filter.userId} onChange={(event) => setFilter({ ...filter, userId: event.target.value })}>
            <option value="">الكل</option>
            {distinctBy(report?.employees, (employee) => employee.userName ?? employee.userId).map((employee) => <option key={employee.userId} value={employee.userId}>{employee.userName ?? "—"}</option>)}
          </select>
        </label>
        <label>الحالة
          <select value={filter.status} onChange={(event) => setFilter({ ...filter, status: event.target.value as ReportFilter["status"] })}>
            <option value="">مثبّت وغير مثبّت</option>
            <option value="Completed">مثبّت</option>
            <option value="Open">غير مثبّت</option>
            <option value="Discarded">ملغى</option>
          </select>
        </label>
        <button type="submit" className="primary-button" disabled={loading}>عرض</button>
      </form>

      {selectedBranch && !selectedBranch.current ? (
        <p className={selectedBranch.stale ? "inventory-hint is-warning" : "inventory-hint"}>
          من نسخة الفرع على المركز. آخر تحديث: {formatDateTime(selectedBranch.lastPushAt)}
          {selectedBranch.stale ? " · النسخة أقدم من 30 دقيقة." : ""}
        </p>
      ) : null}

      <div className="inventory-list">
        {loading ? <Waiting compact label="جاري تحميل التقرير..." /> : null}
        {!loading && report && report.sessions.length === 0 ? <p className="inventory-hint">لا يوجد جرد في هذه الفترة.</p> : null}
        {!loading && report ? sessionsPaging.visible.map((entry) => (
          <button key={entry.sessionId} type="button" className="inventory-report-row" onClick={() => setView({ kind: "session", sessionId: entry.sessionId, branchId: report.remote ? report.branchId : "" })}>
            <span className="inventory-report-title">
              <strong>{entry.inventoryTypeLabel}</strong>
              {entry.pages.length > 0 ? <small>الصفحات: {entry.pages.join("، ")}</small> : null}
            </span>
            <span>{entry.userName ?? "—"} · {entry.businessDate}</span>
            <span>تم جرد {entry.countedItems}{entry.inventoryType === "Manual" ? "" : ` من ${entry.totalItems}`} ({entry.completionPercent}%)</span>
            <span className={entry.differences > 0 ? "inventory-badge is-warning" : "inventory-badge"}>الفروقات: {entry.differences}</span>
            <span className="inventory-badge">{sessionStatusLabel(entry.status)}{entry.audits > 0 ? ` · مُدقق ${entry.audits}` : ""}</span>
          </button>
        )) : null}
        {!loading && report ? <Pager {...sessionsPaging} label="جرد" /> : null}
      </div>
    </div>
  );
}

function SessionDetailView({ client, sessionId, branchId, onMessage, onBusy, onOpenAudit }: {
  client: InventoryReviewClient;
  sessionId: string;
  branchId: string;
  onMessage: (message: string) => void;
  onBusy: (label: string) => void;
  onOpenAudit: (auditId: string) => void;
}) {
  const [detail, setDetail] = useState<SessionDetail | null>(null);
  const [tab, setTab] = useState<ReportLineTab>("differences");
  const [picking, setPicking] = useState(false);
  const [picked, setPicked] = useState<string[]>([]);

  useEffect(() => {
    let cancelled = false;
    onMessage("");
    void client.session(sessionId, branchId)
      .then((value) => { if (!cancelled) setDetail(value); })
      .catch((error: Error) => { if (!cancelled) onMessage(error.message); });
    return () => { cancelled = true; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [client, sessionId, branchId]);

  const lines = useMemo(() => filterReportLines(detail?.items ?? [], picking ? "counted" : tab), [detail, tab, picking]);
  const linesPaging = usePaging(lines, `${tab}|${picking}`);

  async function startAudit(mode: AuditMode) {
    if (mode === "Manual" && !picking) { setPicking(true); setPicked([]); return; }
    onBusy("جاري بدء التدقيق...");
    onMessage("");
    try {
      const audit = await client.startAudit(sessionId, mode, mode === "Manual" ? picked : []);
      onOpenAudit(audit.audit.auditId);
    } catch (error) {
      onMessage((error as Error).message);
    } finally {
      onBusy("");
    }
  }

  if (!detail) return <Waiting compact label="جاري تحميل الجرد..." />;
  const { session } = detail;
  const counted = detail.items.filter((line) => line.isCounted).length;
  const differences = filterReportLines(detail.items, "differences").length;
  const openAudit = detail.audits.find((audit) => audit.status === "Open");

  return (
    <div className="inventory-report">
      <header className="inventory-count-head">
        <div>
          <strong>{session.inventoryTypeLabel}</strong>
          <span>{session.userName ?? "—"} · {session.businessDate} · {sessionStatusLabel(session.status)}</span>
        </div>
        <span className="inventory-progress">
          تم جرد {counted}{session.inventoryType === "Manual" ? "" : ` من ${session.totalItems}`} · الفروقات {differences}
        </span>
      </header>

      {detail.remote ? <p className="inventory-hint">جرد فرع آخر من نسخته على المركز: للعرض فقط، والتدقيق يتم في الفرع نفسه.</p> : null}
      {!detail.remote && session.status === "Completed" ? (
        <section className="inventory-audit-start" aria-label="تدقيق المشرف">
          <strong>تدقيق المشرف</strong>
          <p className="inventory-hint">عدّ أعمى للأصناف المعدودة، ثم مقارنة بعدّ الموظف. التدقيق لا يغيّر المخزون.</p>
          {picking ? (
            <div className="inventory-audit-actions">
              <span>تم اختيار {picked.length} صنف</span>
              <button type="button" className="primary-button" disabled={picked.length === 0} onClick={() => void startAudit("Manual")}>بدء التدقيق</button>
              <button type="button" className="ghost-button" onClick={() => { setPicking(false); setPicked([]); }}>إلغاء</button>
            </div>
          ) : (
            <div className="inventory-audit-actions">
              <button type="button" className="ghost-button" onClick={() => void startAudit("All")}>كل المعدود</button>
              <button type="button" className="ghost-button" disabled={differences === 0} onClick={() => void startAudit("Differences")}>الفروقات فقط</button>
              <button type="button" className="ghost-button" onClick={() => void startAudit("Manual")}>اختيار يدوي</button>
            </div>
          )}
          {openAudit && !picking ? <p className="inventory-hint">يوجد تدقيق غير منتهٍ. «بدء» لنفس المشرف يتابعه.</p> : null}
        </section>
      ) : null}
      {!detail.remote && session.status !== "Completed" ? <p className="inventory-hint">يُدقق الجرد بعد تثبيته فقط.</p> : null}

      {detail.audits.length > 0 && !picking ? (
        <section className="inventory-audits" aria-label="التدقيقات">
          {detail.audits.map((audit) => (
            <button key={audit.auditId} type="button" className="inventory-report-row" disabled={detail.remote} onClick={() => onOpenAudit(audit.auditId)}>
              <strong>تدقيق: {audit.auditModeLabel}</strong>
              <span>{audit.userName ?? "—"} · {formatDateTime(audit.startedAt)}</span>
              <span>{audit.status === "Completed" ? `متطابق ${audit.matchedItems} من ${audit.countedItems}` : `دُقق ${audit.countedItems} من ${audit.totalItems} (غير منتهٍ)`}</span>
            </button>
          ))}
        </section>
      ) : null}

      {!picking ? (
        <div className="inventory-tabs" role="tablist">
          {([["differences", "الفروقات"], ["counted", "المعدود"], ["uncounted", "غير المعدود"]] as [ReportLineTab, string][]).map(([value, label]) => (
            <button key={value} type="button" role="tab" aria-selected={tab === value} className={tab === value ? "is-active" : undefined} onClick={() => setTab(value)}>{label}</button>
          ))}
        </div>
      ) : null}

      <div className="inventory-list">
        {lines.length === 0 ? <p className="inventory-hint">لا توجد أصناف في هذا التبويب.</p> : null}
        {linesPaging.visible.map((line) => (
          <article key={line.sessionItemId} className={picking && picked.includes(line.sessionItemId) ? "inventory-row is-selected" : "inventory-row"}>
            <div className="inventory-row-name">
              <span className="inventory-number">{line.itemNumber}</span>
              <strong>{line.itemName || "صنف بدون اسم"}</strong>
              {line.countedByName ? <small>{line.countedByName} · {formatDateTime(line.countedAt)}</small> : null}
            </div>
            <div className="inventory-counted">
              {picking ? (
                <label className={picked.includes(line.sessionItemId) ? "inventory-pick is-selected" : "inventory-pick"}>
                  <input
                    type="checkbox"
                    checked={picked.includes(line.sessionItemId)}
                    onChange={() => setPicked((current) => current.includes(line.sessionItemId)
                      ? current.filter((value) => value !== line.sessionItemId)
                      : [...current, line.sessionItemId])}
                  />
                  تدقيق
                </label>
              ) : null}
              <span>المعدود: {formatQuantity(line.countedQuantity)}</span>
              <span>المفترض: {formatQuantity(line.expectedQuantity)}</span>
              <span>الفارق: {formatDifference(line.difference)}</span>
              {line.posted ? <span className="inventory-badge">مرحّل</span> : null}
            </div>
          </article>
        ))}
        <Pager {...linesPaging} />
      </div>
    </div>
  );
}

function AuditPanel({ client, auditId, onMessage, onBusy, onConfirm }: {
  client: InventoryReviewClient;
  auditId: string;
  onMessage: (message: string) => void;
  onBusy: (label: string) => void;
  onConfirm: (confirmation: PanelConfirmation) => void;
}) {
  const [view, setView] = useState<AuditView | null>(null);
  const [drafts, setDrafts] = useState<Record<string, string>>({});
  const [saving, setSaving] = useState<string | null>(null);
  const [query, setQuery] = useState("");

  useEffect(() => {
    let cancelled = false;
    onMessage("");
    void client.audit(auditId)
      .then((value) => { if (!cancelled) setView(value); })
      .catch((error: Error) => { if (!cancelled) onMessage(error.message); });
    return () => { cancelled = true; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [client, auditId]);

  const visible = useMemo(() => {
    const text = normalizeDigits(query.trim()).toLowerCase();
    const lines = view?.lines ?? [];
    return text ? lines.filter((line) => line.itemNumber.toLowerCase().includes(text) || (line.itemName ?? "").toLowerCase().includes(text)) : lines;
  }, [view, query]);
  const auditPaging = usePaging(visible, query);

  if (!view) return <Waiting compact label="جاري تحميل التدقيق..." />;
  const { audit } = view;
  const editable = audit.status === "Open" && audit.mine;

  async function save(line: AuditLine) {
    const quantity = parseCountInput(drafts[line.auditItemId] ?? "");
    if (quantity === null) { onMessage("أدخل كمية صحيحة. الصفر مسموح والسالبة غير مسموحة."); return; }
    setSaving(line.auditItemId);
    onMessage("");
    try {
      setView(await client.auditCount(auditId, line.auditItemId, quantity));
      setDrafts((current) => { const next = { ...current }; delete next[line.auditItemId]; return next; });
    } catch (error) {
      onMessage((error as Error).message);
    } finally {
      setSaving(null);
    }
  }

  function complete() {
    const remaining = audit.totalItems - audit.countedItems;
    onConfirm({
      title: "إنهاء التدقيق",
      message: remaining > 0
        ? `لم تُدقق ${remaining} من الأصناف. بعد الإنهاء تظهر المقارنة ولا يمكن تعديل التدقيق. هل تريد المتابعة؟`
        : "بعد الإنهاء تظهر المقارنة مع عدّ الموظف ولا يمكن تعديل التدقيق. هل تريد المتابعة؟",
      accept: "إنهاء التدقيق",
      onAccept: () => {
        void (async () => {
          onBusy("جاري إنهاء التدقيق...");
          try {
            setView(await client.completeAudit(auditId));
          } catch (error) {
            onMessage((error as Error).message);
          } finally {
            onBusy("");
          }
        })();
      },
    });
  }

  return (
    <div className="inventory-count">
      <header className="inventory-count-head">
        <div>
          <strong>تدقيق: {audit.auditModeLabel}</strong>
          <span>{audit.inventoryTypeLabel} · {audit.employeeName ?? "—"} · {audit.businessDate}</span>
        </div>
        <span className="inventory-progress">
          {audit.status === "Completed"
            ? `متطابق ${audit.matchedItems} من ${audit.countedItems}`
            : `دُقق ${audit.countedItems} من ${audit.totalItems}`}
        </span>
      </header>

      {audit.status === "Open" && !audit.mine ? <p className="inventory-hint">هذا التدقيق لمشرف آخر ولم ينتهِ بعد.</p> : null}
      {editable ? <p className="inventory-hint">تدقيق أعمى: أدخل ما تعدّه أنت. المقارنة تظهر بعد الإنهاء.</p> : null}

      <form className="inventory-search" onSubmit={(event: FormEvent) => event.preventDefault()}>
        <input value={query} onChange={(event) => setQuery(event.target.value)} placeholder="بحث باسم الصنف أو رقم الصنف" aria-label="بحث" />
        {query ? <button type="button" className="ghost-button" aria-label="مسح البحث" onClick={() => setQuery("")}>×</button> : null}
      </form>

      <div className="inventory-list">
        {auditPaging.visible.map((line) => (
          <article key={line.auditItemId} className={line.matched === false ? "inventory-row is-mismatch" : "inventory-row"}>
            <div className="inventory-row-name">
              <span className="inventory-number">{line.itemNumber}</span>
              <strong>{line.itemName || "صنف بدون اسم"}</strong>
            </div>
            {audit.status === "Completed" ? (
              <div className="inventory-counted">
                <span>النظام: {formatQuantity(line.systemQuantity)}</span>
                <span>الموظف: {formatQuantity(line.employeeQuantity)}</span>
                <span>المشرف: {line.isCounted ? formatQuantity(line.auditQuantity) : "لم يُدقق"}</span>
                <span>الفرق: {formatDifference(line.difference)}</span>
                {line.matched === true ? <span className="inventory-badge">متطابق</span> : null}
                {line.matched === false ? <span className="inventory-badge is-warning">غير متطابق</span> : null}
              </div>
            ) : editable ? (
              <div className="inventory-qty">
                {line.isCounted ? <span className="inventory-saved">المشرف: {formatQuantity(line.auditQuantity)}</span> : null}
                <input
                  inputMode="decimal"
                  aria-label={`عدّ المشرف ${line.itemName ?? line.itemNumber}`}
                  value={drafts[line.auditItemId] ?? ""}
                  onChange={(event) => setDrafts((current) => ({ ...current, [line.auditItemId]: event.target.value }))}
                  onKeyDown={(event) => { if (event.key === "Enter") { event.preventDefault(); void save(line); } }}
                />
                <button type="button" className="primary-button" disabled={saving === line.auditItemId} onClick={() => void save(line)}>
                  {saving === line.auditItemId ? "جاري الحفظ..." : "حفظ"}
                </button>
              </div>
            ) : (
              <div className="inventory-counted"><span>{line.isCounted ? "تم التدقيق" : "لم يُدقق"}</span></div>
            )}
          </article>
        ))}
        <Pager {...auditPaging} />
      </div>

      {editable ? (
        <footer className="inventory-actions">
          <button type="button" className="primary-button" disabled={audit.countedItems === 0} onClick={complete}>إنهاء التدقيق</button>
        </footer>
      ) : null}
      {editable ? <DesktopKeyboard /> : null}
    </div>
  );
}

// ======================================================================
// معالجة الأصناف: التصنيف و«كمية صنف»
// ======================================================================

export function ItemProcessingPanel({ client, canClassify, canCorrect, onMessage, onBusy, onConfirm }: {
  client: InventoryReviewClient;
  canClassify: boolean;
  canCorrect: boolean;
  onMessage: (message: string) => void;
  onBusy: (label: string) => void;
  onConfirm: (confirmation: PanelConfirmation) => void;
}) {
  const [code, setCode] = useState("");
  const [item, setItem] = useState<ItemProcessing | null>(null);
  const [important, setImportant] = useState(false);
  const [page, setPage] = useState("");
  const [supplyDays, setSupplyDays] = useState("");
  const [quantity, setQuantity] = useState("");
  const [reason, setReason] = useState("");
  const [correctionId, setCorrectionId] = useState(() => newRequestId());
  const [notice, setNotice] = useState("");

  function show(next: ItemProcessing) {
    setItem(next);
    setImportant(next.isImportant);
    setPage(next.inventoryPage ? String(next.inventoryPage) : "");
    setSupplyDays(next.supplyDays ? String(next.supplyDays) : "");
  }

  async function find(event: FormEvent) {
    event.preventDefault();
    const text = code.trim();
    if (!text) return;
    onMessage("");
    setNotice("");
    onBusy("جاري البحث عن الصنف...");
    try {
      show(await client.item(text));
      setQuantity("");
      setReason("");
      setCorrectionId(newRequestId());
      setCode("");
    } catch (error) {
      setItem(null);
      onMessage((error as Error).message);
    } finally {
      onBusy("");
    }
  }

  async function saveClassification() {
    if (!item) return;
    const days = parseOptionalInteger(supplyDays, 1, 365);
    if (days === undefined) { onMessage("أيام التزويد يجب أن تكون من 1 إلى 365، أو فارغة."); return; }
    const inventoryPage = page ? Number(page) : null;
    onMessage("");
    setNotice("");
    onBusy("جاري حفظ التصنيف...");
    try {
      show(await client.classify(item.itemId, important, inventoryPage, days));
      setNotice("تم حفظ التصنيف في المركز، وسيصل إلى باقي الفروع خلال دقيقة.");
    } catch (error) {
      onMessage((error as Error).message);
    } finally {
      onBusy("");
    }
  }

  function correct() {
    if (!item) return;
    const value = parseCountInput(quantity);
    if (value === null) { onMessage("أدخل الكمية الصحيحة. الصفر مسموح والسالبة غير مسموحة."); return; }
    if (reason.trim().length < 3) { onMessage("يرجى كتابة سبب التصحيح."); return; }
    const before = item.quantityKnown ? formatQuantity(item.onHandQuantity) : "غير معروفة";
    onConfirm({
      title: "تصحيح الكمية",
      message: `سيتم تغيير رصيد ${item.itemName ?? item.itemNumber} في هذا الفرع من ${before} إلى ${formatQuantity(value)}. هل تريد المتابعة؟`,
      accept: "تصحيح",
      onAccept: () => {
        void (async () => {
          onMessage("");
          setNotice("");
          onBusy("جاري تصحيح الكمية...");
          try {
            show(await client.correctQuantity(item.itemId, correctionId, value, reason.trim()));
            setQuantity("");
            setReason("");
            setCorrectionId(newRequestId());
            setNotice("تم تصحيح الكمية.");
          } catch (error) {
            onMessage((error as Error).message);
          } finally {
            onBusy("");
          }
        })();
      },
    });
  }

  return (
    <div className="inventory-items">
      <form className="inventory-search" onSubmit={(event) => void find(event)}>
        <input value={code} onChange={(event) => setCode(event.target.value)} placeholder="رقم الصنف / باركود" aria-label="رقم الصنف" />
        <button type="submit" className="ghost-button">بحث</button>
      </form>

      {notice ? <p className="inventory-notice" role="status">{notice}</p> : null}
      {!item ? <p className="inventory-hint">اكتب رقم الصنف أو امسح الباركود ثم اضغط «بحث».</p> : null}

      {item ? (
        <>
          <article className="inventory-row">
            <div className="inventory-row-name">
              <span className="inventory-number">{item.itemNumber}</span>
              <strong>{item.itemName || "صنف بدون اسم"}</strong>
              {item.convertedFromPackage ? <small>تم تحويل {item.scannedItemNumber} إلى وحدة الجرد الأساسية.</small> : null}
            </div>
          </article>

          <section className="inventory-section" aria-label="التصنيف">
            <strong>التصنيف</strong>
            <label className="inventory-pick">
              <input type="checkbox" checked={important} disabled={!canClassify} onChange={(event) => setImportant(event.target.checked)} />
              صنف مهم
            </label>
            <label>صفحة الجرد الدوري
              <select value={page} disabled={!canClassify} onChange={(event) => setPage(event.target.value)}>
                <option value="">بلا صفحة</option>
                {Array.from({ length: 12 }, (_, index) => index + 1).map((value) => <option key={value} value={value}>الصفحة {value}</option>)}
              </select>
            </label>
            <label>أيام التزويد
              <input inputMode="numeric" value={supplyDays} disabled={!canClassify} onChange={(event) => setSupplyDays(event.target.value)} aria-label="أيام التزويد" />
            </label>
            {item.classifiedAt ? <small className="inventory-hint">آخر تعديل: {item.classifiedBy ?? "—"} · {formatDateTime(item.classifiedAt)}</small> : null}
            {canClassify ? <button type="button" className="primary-button" onClick={() => void saveClassification()}>حفظ التصنيف</button> : null}
          </section>

          <section className="inventory-section" aria-label="كمية صنف">
            <strong>كمية صنف</strong>
            <span>الكمية الحالية في الفرع: {item.quantityKnown ? formatQuantity(item.onHandQuantity) : "غير معروفة"}</span>
            {canCorrect ? (
              <>
                <label>الكمية الصحيحة
                  <input inputMode="decimal" value={quantity} onChange={(event) => { setQuantity(event.target.value); setCorrectionId(newRequestId()); }} aria-label="الكمية الصحيحة" />
                </label>
                <label>سبب التصحيح
                  <input value={reason} maxLength={500} onChange={(event) => { setReason(event.target.value); setCorrectionId(newRequestId()); }} aria-label="سبب التصحيح" />
                </label>
                <button type="button" className="primary-button" onClick={correct}>تصحيح الكمية</button>
              </>
            ) : null}
            {item.corrections.length > 0 ? (
              <ul className="inventory-history" aria-label="آخر التصحيحات">
                {item.corrections.map((entry) => (
                  <li key={`${entry.correctedAt}-${entry.newQuantity}`}>
                    {formatDateTime(entry.correctedAt)} · {entry.userName ?? "—"}: {formatQuantity(entry.previousQuantity)} ← {formatQuantity(entry.newQuantity)}
                    {entry.deltaQuantity !== null && entry.deltaQuantity !== undefined
                      ? ` · المفترضة ${formatQuantity(entry.expectedQuantity)}، المعدودة ${formatQuantity(entry.countedQuantity)}، الفارق ${formatDifference(entry.deltaQuantity)}`
                      : ""} ({entry.reason})
                  </li>
                ))}
              </ul>
            ) : null}
          </section>
        </>
      ) : null}
      <DesktopKeyboard />
    </div>
  );
}
