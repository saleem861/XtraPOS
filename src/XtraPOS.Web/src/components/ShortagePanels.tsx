import { useCallback, useEffect, useMemo, useState } from "react";
import type { InventoryType } from "../api/inventory";
import { newRequestId } from "../api/inventoryReview";
import {
  isPurchaseRow,
  type AuditRequest,
  type CustomInventory,
  type EfficiencyRow,
  type FollowUpTab,
  type FollowUpView,
  type ItemBranchState,
  type ProcessingReport,
  type RunHeader,
  type PurchaseRow,
  type RunItem,
  type ShortageClient,
  type ShortageDecisionBody,
  type ShortageOptions,
  type Supplier,
  type SupplyRequest,
  type UserOption,
} from "../api/shortages";
import {
  formatDateTime,
  formatQuantity,
  normalizeDigits,
  parseCountInput,
  parseItemCodes,
  parseOptionalInteger,
  parseOptionalQuantity,
  SHORTAGE_TYPES,
  togglePage,
} from "../inventory/inventoryRules";
import type { BackHandler, PanelConfirmation } from "./InventoryReviewPanels";
import { Waiting } from "./Waiting";

interface PanelProps {
  client: ShortageClient;
  onMessage: (message: string) => void;
  onBusy: (label: string) => void;
  onConfirm: (confirmation: PanelConfirmation) => void;
}

/** تشغيل طلب مع مؤشر الانتظار ورسالة الخطأ الموحّدة. */
function useAction(onMessage: (message: string) => void, onBusy: (label: string) => void) {
  return useCallback(async <T,>(label: string, action: () => Promise<T>): Promise<T | undefined> => {
    onBusy(label);
    onMessage("");
    try {
      return await action();
    } catch (error) {
      onMessage((error as Error).message);
      return undefined;
    } finally {
      onBusy("");
    }
  }, [onMessage, onBusy]);
}

function DateRange({ from, to, onChange, onSubmit }: { from: string; to: string; onChange: (from: string, to: string) => void; onSubmit: () => void }) {
  return (
    <form className="inventory-filter" onSubmit={(event) => { event.preventDefault(); onSubmit(); }}>
      <label>من تاريخ<input type="date" value={from} onChange={(event) => onChange(event.target.value, to)} /></label>
      <label>إلى تاريخ<input type="date" value={to} onChange={(event) => onChange(from, event.target.value)} /></label>
      <button type="submit" className="primary-button">عرض</button>
    </form>
  );
}

// ======================================================================
// معالجة النواقص
// ======================================================================

export function ShortageProcessingPanel({ client, onMessage, onBusy, onConfirm, onRegisterBack }: PanelProps & { onRegisterBack: (handler: BackHandler | null) => void }) {
  const run = useAction(onMessage, onBusy);
  const [options, setOptions] = useState<ShortageOptions | null>(null);
  const [runId, setRunId] = useState("");
  const [finished, setFinished] = useState<{ tasks: number; items: number } | null>(null);

  const loadOptions = useCallback(async () => {
    const value = await run("جاري تحميل الفروع...", () => client.options());
    if (value) setOptions(value);
  }, [client, run]);

  useEffect(() => { void loadOptions(); }, [loadOptions]);

  useEffect(() => {
    onRegisterBack(() => {
      if (runId) { setRunId(""); void loadOptions(); return true; }
      if (finished) { setFinished(null); return true; }
      return false;
    });
    return () => onRegisterBack(null);
  }, [runId, finished, loadOptions, onRegisterBack]);

  if (finished) {
    return (
      <div className="inventory-done">
        <strong>تم التثبيت النهائي.</strong>
        <p>تمت معالجة {finished.items} صنف، وأُنشئت {finished.tasks} مهمة في «متابعة النواقص».</p>
        <button type="button" className="primary-button" onClick={() => { setFinished(null); void loadOptions(); }}>معالجة جديدة</button>
      </div>
    );
  }

  if (runId) {
    return (
      <RunView
        client={client}
        runId={runId}
        onMessage={onMessage}
        onBusy={onBusy}
        onConfirm={onConfirm}
        onFinished={(result) => { setRunId(""); setFinished(result); }}
        onDiscarded={() => { setRunId(""); void loadOptions(); }}
      />
    );
  }

  if (!options) return <Waiting compact label="جاري التحميل..." />;
  return (
    <ShortageSetup
      options={options}
      onConfirm={onConfirm}
      onMessage={onMessage}
      onResume={(id) => setRunId(id)}
      onStart={async (request) => {
        const result = await run("جاري حساب النواقص من نسخ الفروع...", () => client.start(request));
        if (result?.draft) setRunId(result.draft.runId);
      }}
    />
  );
}

function ShortageSetup({ options, onConfirm, onMessage, onResume, onStart }: {
  options: ShortageOptions;
  onConfirm: (confirmation: PanelConfirmation) => void;
  onMessage: (message: string) => void;
  onResume: (runId: string) => void;
  onStart: (request: Parameters<ShortageClient["start"]>[0]) => Promise<void>;
}) {
  const [type, setType] = useState<InventoryType>("Important");
  const [branchIds, setBranchIds] = useState<string[]>(() => options.branches.map((branch) => branch.branchId));
  const [pages, setPages] = useState<number[]>([1]);
  const [days, setDays] = useState("7");
  const [customId, setCustomId] = useState(options.customInventories[0]?.customInventoryId ?? "");
  const [manualFrom, setManualFrom] = useState("");
  const [manualTo, setManualTo] = useState("");
  const [userIds, setUserIds] = useState<string[]>([]);

  function submit(replaceDraft: boolean) {
    const supplyDays = parseOptionalInteger(days, 1, 365);
    if (!supplyDays) { onMessage("أيام التوفير يجب أن تكون من 1 إلى 365."); return; }
    if (branchIds.length === 0) { onMessage("يرجى اختيار فرع واحد على الأقل."); return; }
    const request = {
      shortageType: type,
      branchIds,
      pages: type === "Periodic" ? pages : [],
      supplyDays,
      customInventoryId: type === "Custom" ? customId || null : null,
      manualFrom: type === "Manual" ? manualFrom || null : null,
      manualTo: type === "Manual" ? manualTo || null : null,
      userIds: type === "Manual" ? userIds : [],
      replaceDraft,
    };
    const stale = options.branches.filter((branch) => branchIds.includes(branch.branchId) && branch.stale && !branch.current);
    const go = () => { void onStart(request); };
    if (stale.length > 0) {
      onConfirm({
        title: "نسخة فرع قديمة",
        message: `نسخة ${stale.map((branch) => branch.branchName).join("، ")} على المركز أقدم من 30 دقيقة. قد لا تكون الأرصدة الحالية. هل تتابع؟`,
        accept: "متابعة",
        onAccept: go,
      });
      return;
    }
    go();
  }

  return (
    <div className="inventory-report">
      {options.draft ? (
        <section className="inventory-section" aria-label="معالجة غير مثبتة">
          <strong>لديك معالجة غير مثبتة</strong>
          <span>{options.draft.shortageTypeLabel} · {options.draft.supplyDays} يوم · عولج {options.draft.processedCount} من {options.draft.itemCount}</span>
          <span className="inventory-hint">آخر حفظ: {formatDateTime(options.draft.lastSavedAt)}</span>
          <button type="button" className="primary-button" onClick={() => onResume(options.draft!.runId)}>متابعة</button>
          <button
            type="button"
            className="ghost-button"
            onClick={() => onConfirm({ title: "معالجة جديدة", message: "ستُلغى المعالجة غير المثبتة ويُحسب من جديد. هل تريد المتابعة؟", accept: "جديد", onAccept: () => submit(true) })}
          >
            جديد
          </button>
        </section>
      ) : null}

      <section className="inventory-section" aria-label="نوع النواقص">
        <strong>النوع</strong>
        {SHORTAGE_TYPES.map((option) => (
          <label key={option.value} className="inventory-pick">
            <input type="radio" name="shortage-type" checked={type === option.value} onChange={() => setType(option.value)} />
            {option.label}
          </label>
        ))}
      </section>

      <section className="inventory-section" aria-label="الفروع">
        <strong>الفروع</strong>
        {options.branches.map((branch) => (
          <label key={branch.branchId} className="inventory-pick">
            <input
              type="checkbox"
              checked={branchIds.includes(branch.branchId)}
              onChange={() => setBranchIds((current) => current.includes(branch.branchId) ? current.filter((id) => id !== branch.branchId) : [...current, branch.branchId])}
            />
            {branch.branchName}
            <small className={branch.stale && !branch.current ? "inventory-hint is-warning" : "inventory-hint"}>
              {branch.lastPushAt ? `آخر نسخة ${formatDateTime(branch.lastPushAt)}` : "لم تصل نسخة بعد"}
            </small>
          </label>
        ))}
      </section>

      {type === "Periodic" ? (
        <section className="inventory-section" aria-label="الصفحات">
          <strong>الصفحات</strong>
          {Array.from({ length: options.pageCount }, (_, index) => index + 1).map((page) => (
            <button key={page} type="button" className={pages.includes(page) ? "inventory-page is-selected" : "inventory-page"} onClick={() => setPages((current) => togglePage(current, page))}>
              {page}
            </button>
          ))}
        </section>
      ) : null}

      {type === "Custom" ? (
        <section className="inventory-section" aria-label="الجرد المخصص">
          <label>الجرد المخصص
            <select value={customId} onChange={(event) => setCustomId(event.target.value)}>
              {options.customInventories.length === 0 ? <option value="">لا يوجد جرد مخصص</option> : null}
              {options.customInventories.map((custom) => <option key={custom.customInventoryId} value={custom.customInventoryId}>{custom.name} ({custom.items})</option>)}
            </select>
          </label>
        </section>
      ) : null}

      {type === "Manual" ? (
        <section className="inventory-section" aria-label="الفترة والموظفون">
          <strong>الفترة والموظفون</strong>
          <label>من تاريخ<input type="date" value={manualFrom} onChange={(event) => setManualFrom(event.target.value)} /></label>
          <label>إلى تاريخ<input type="date" value={manualTo} onChange={(event) => setManualTo(event.target.value)} /></label>
          <div className="inventory-checklist">
            {options.users.map((user) => (
              <label key={user.userId} className="inventory-pick">
                <input
                  type="checkbox"
                  checked={userIds.includes(user.userId)}
                  onChange={() => setUserIds((current) => current.includes(user.userId) ? current.filter((id) => id !== user.userId) : [...current, user.userId])}
                />
                {user.displayName}
              </label>
            ))}
          </div>
          <small className="inventory-hint">بدون اختيار موظف = كل الموظفين. الكمية = آخر عدّ مكتمل في الفترة.</small>
        </section>
      ) : null}

      <section className="inventory-section" aria-label="أيام التوفير">
        <label>عدد أيام التوفير
          <input inputMode="numeric" value={days} onChange={(event) => setDays(event.target.value)} aria-label="عدد أيام التوفير" />
        </label>
        <button
          type="button"
          className="primary-button"
          onClick={() => options.draft
            ? onConfirm({ title: "معالجة جديدة", message: "لديك معالجة غير مثبتة وستُلغى ويُحسب من جديد. هل تريد المتابعة؟", accept: "جديد", onAccept: () => submit(true) })
            : submit(false)}
        >
          تثبيت ومتابعة
        </button>
      </section>
    </div>
  );
}

type RunFilter = "open" | "processed" | "all";

function RunView({ client, runId, onMessage, onBusy, onConfirm, onFinished, onDiscarded }: PanelProps & {
  runId: string;
  onFinished: (result: { tasks: number; items: number }) => void;
  onDiscarded: () => void;
}) {
  const run = useAction(onMessage, onBusy);
  const [data, setData] = useState<{ run: RunHeader; items: RunItem[] } | null>(null);
  const [filter, setFilter] = useState<RunFilter>("open");
  const [query, setQuery] = useState("");
  const [expanded, setExpanded] = useState("");

  const load = useCallback(async () => {
    const value = await run("جاري تحميل الأصناف...", () => client.run(runId));
    if (value) setData(value);
  }, [client, runId, run]);

  useEffect(() => { void load(); }, [load]);

  const visible = useMemo(() => {
    const text = normalizeDigits(query.trim()).toLowerCase();
    return (data?.items ?? []).filter((item) =>
      (filter === "all" || (filter === "open" ? !item.isProcessed : item.isProcessed))
      && (!text || item.itemNumber.toLowerCase().includes(text) || (item.itemName ?? "").toLowerCase().includes(text)));
  }, [data, filter, query]);

  if (!data) return <Waiting compact label="جاري التحميل..." />;
  const header = data.run;
  const processed = data.items.filter((item) => item.isProcessed).length;
  const editable = header.status === "Draft" && header.mine;

  function replace(itemId: string, change: Partial<RunItem>) {
    setData((current) => current ? { ...current, items: current.items.map((item) => item.itemId === itemId ? { ...item, ...change } : item) } : current);
  }

  return (
    <div className="inventory-count">
      <header className="inventory-count-head">
        <div>
          <strong>{header.shortageTypeLabel}{header.customInventoryName ? `: ${header.customInventoryName}` : ""}</strong>
          <span>{header.supplyDays} يوم توفير · {header.branches.map((branch) => branch.branchName).join("، ")}</span>
        </div>
        <span className="inventory-progress">عولج {processed} من {data.items.length}</span>
      </header>
      {header.branches.some((branch) => branch.stale) ? <p className="inventory-hint is-warning">بعض نسخ الفروع كانت أقدم من 30 دقيقة لحظة الحساب.</p> : null}

      <div className="inventory-tabs" role="tablist">
        {([["open", "النواقص الحالية"], ["processed", "المعالج"], ["all", "عرض الكل"]] as [RunFilter, string][]).map(([value, label]) => (
          <button key={value} type="button" role="tab" aria-selected={filter === value} className={filter === value ? "is-active" : undefined} onClick={() => setFilter(value)}>{label}</button>
        ))}
      </div>
      <form className="inventory-search" onSubmit={(event) => event.preventDefault()}>
        <input value={query} onChange={(event) => setQuery(event.target.value)} placeholder="بحث باسم الصنف أو رقم الصنف" aria-label="بحث" />
      </form>

      <div className="inventory-list">
        {data.items.length === 0 ? <p className="inventory-hint">لا توجد نواقص في هذا النطاق. يمكنك التثبيت مباشرة.</p> : null}
        {visible.map((item) => (
          <ShortageItemRow
            key={item.itemId}
            client={client}
            runId={runId}
            item={item}
            editable={editable}
            expanded={expanded === item.itemId}
            onToggle={() => setExpanded((current) => current === item.itemId ? "" : item.itemId)}
            onSaved={(change) => replace(item.itemId, change)}
            onMessage={onMessage}
            onBusy={onBusy}
            onConfirm={onConfirm}
          />
        ))}
      </div>

      {editable ? (
        <footer className="inventory-actions">
          <button
            type="button"
            className="ghost-button"
            onClick={() => onConfirm({
              title: "إلغاء المعالجة",
              message: "ستُلغى هذه المعالجة ولن تُنشأ أي مهام. هل تريد المتابعة؟",
              accept: "إلغاء المعالجة",
              onAccept: () => { void run("جاري الإلغاء...", () => client.discard(runId)).then((value) => { if (value) onDiscarded(); }); },
            })}
          >
            إلغاء المعالجة
          </button>
          <button
            type="button"
            className="primary-button"
            onClick={() => onConfirm({
              title: "التثبيت النهائي",
              message: processed < data.items.length
                ? `لم تتم معالجة ${data.items.length - processed} صنف. عالج كل الأصناف قبل التثبيت.`
                : "سيتم تثبيت المعالجة وإنشاء مهام الشراء والمخزن والموازنة والحذف. هل تريد المتابعة؟",
              accept: "تثبيت نهائي",
              onAccept: () => {
                if (processed < data.items.length) return;
                void run("جاري التثبيت النهائي...", () => client.finalize(runId)).then((value) => { if (value) onFinished(value); });
              },
            })}
          >
            تثبيت نهائي
          </button>
        </footer>
      ) : null}
    </div>
  );
}

function ShortageItemRow({ client, runId, item, editable, expanded, onToggle, onSaved, onMessage, onBusy, onConfirm }: PanelProps & {
  runId: string;
  item: RunItem;
  editable: boolean;
  expanded: boolean;
  onToggle: () => void;
  onSaved: (change: Partial<RunItem>) => void;
}) {
  const run = useAction(onMessage, onBusy);
  const [purchase, setPurchase] = useState(() => String(item.purchaseQuantity ?? (item.isProcessed ? "" : item.requiredQuantity || "")));
  const [warehouse, setWarehouse] = useState(() => String(item.warehouseQuantity ?? ""));
  const [balance, setBalance] = useState(() => String(item.balanceDecision ?? (item.isProcessed ? "" : item.balanceQuantity || "")));
  const [note, setNote] = useState(item.note ?? "");

  async function save(kind: "quantities" | "delete" | "supplied" | "clear") {
    let body: ShortageDecisionBody;
    if (kind === "clear") {
      body = { purchaseQuantity: null, warehouseQuantity: null, balanceQuantity: null, delete: false, alreadySupplied: false, note: null, clear: true };
    } else if (kind === "quantities") {
      const values = [parseOptionalQuantity(purchase), parseOptionalQuantity(warehouse), parseOptionalQuantity(balance)];
      if (values.some((value) => value === undefined)) { onMessage("أدخل كميات صحيحة. الصفر والفارغ يعنيان لا شيء."); return; }
      const [p, w, b] = values as (number | null)[];
      body = { purchaseQuantity: p, warehouseQuantity: w, balanceQuantity: b, delete: false, alreadySupplied: false, note: note || null };
    } else {
      body = { purchaseQuantity: null, warehouseQuantity: null, balanceQuantity: null, delete: kind === "delete", alreadySupplied: kind === "supplied", note: note || null };
    }

    const saved = await run("جاري الحفظ...", () => client.decide(runId, item.itemId, body));
    if (!saved) return;
    onSaved(kind === "clear"
      ? { isProcessed: false, purchaseQuantity: null, warehouseQuantity: null, balanceDecision: null, deleteRequested: false, alreadySupplied: false, note: null }
      : {
          isProcessed: true,
          purchaseQuantity: body.purchaseQuantity,
          warehouseQuantity: body.warehouseQuantity,
          balanceDecision: body.balanceQuantity,
          deleteRequested: body.delete,
          alreadySupplied: body.alreadySupplied,
          note: body.note,
        });
  }

  const decision = item.deleteRequested ? "طلب حذف"
    : item.alreadySupplied ? "تم التزويد"
      : [item.purchaseQuantity ? `شراء ${formatQuantity(item.purchaseQuantity)}` : "", item.warehouseQuantity ? `مخزن ${formatQuantity(item.warehouseQuantity)}` : "", item.balanceDecision ? `موازنة ${formatQuantity(item.balanceDecision)}` : ""].filter(Boolean).join(" · ");

  return (
    <article className={item.isProcessed ? "inventory-row is-processed" : "inventory-row"}>
      <div className="inventory-row-name">
        <span className="inventory-number">{item.itemNumber}</span>
        <strong>{item.itemName || "صنف بدون اسم"}</strong>
        <small>
          احتياج {formatQuantity(item.totalNeed)} · فائض {formatQuantity(item.totalSurplus)} · مطلوب شراء {formatQuantity(item.requiredQuantity)} · موازنة {formatQuantity(item.balanceQuantity)}
        </small>
        {item.missingRate ? <small className="inventory-hint is-warning">سرعة البيع غير محسوبة في فرع أو أكثر (حُسبت صفراً).</small> : null}
        {item.missingQuantity ? <small className="inventory-hint is-warning">الرصيد غير معروف في فرع أو أكثر (حُسب صفراً).</small> : null}
        {item.isProcessed ? <small className="inventory-saved">القرار: {decision || "—"}{item.processedByName ? ` · ${item.processedByName}` : ""}</small> : null}
      </div>
      <div className="inventory-counted">
        <button type="button" className="ghost-button" onClick={onToggle}>{expanded ? "إخفاء" : "الكميات"}</button>
      </div>
      {expanded ? (
        <div className="inventory-expand">
          <table className="inventory-table">
            <thead><tr><th>الفرع</th><th>الرصيد</th><th>السرعة</th><th>الهدف</th><th>احتياج</th><th>فائض</th></tr></thead>
            <tbody>
              {item.branches.map((branch) => (
                <tr key={branch.branchId}>
                  <td>{branch.branchName ?? "—"}</td>
                  <td>{formatQuantity(branch.currentQuantity)}</td>
                  <td>{branch.dailySalesRate === null ? "غير محسوبة" : formatQuantity(branch.dailySalesRate)}</td>
                  <td>{formatQuantity(branch.target)}</td>
                  <td>{formatQuantity(branch.need)}</td>
                  <td>{formatQuantity(branch.surplus)}</td>
                </tr>
              ))}
            </tbody>
          </table>
          <BranchCountsAndCorrection client={client} itemId={item.itemId} onMessage={onMessage} onBusy={onBusy} onConfirm={onConfirm} />
          {editable ? (
            <div className="inventory-decision">
              <label>شراء<input inputMode="decimal" value={purchase} onChange={(event) => setPurchase(event.target.value)} aria-label={`شراء ${item.itemNumber}`} /></label>
              <label>المخزن<input inputMode="decimal" value={warehouse} onChange={(event) => setWarehouse(event.target.value)} aria-label={`المخزن ${item.itemNumber}`} /></label>
              <label>موازنة<input inputMode="decimal" value={balance} onChange={(event) => setBalance(event.target.value)} aria-label={`موازنة ${item.itemNumber}`} /></label>
              <label>ملاحظة<input value={note} maxLength={500} onChange={(event) => setNote(event.target.value)} aria-label={`ملاحظة ${item.itemNumber}`} /></label>
              <button type="button" className="primary-button" onClick={() => void save("quantities")}>حفظ</button>
              <button type="button" className="ghost-button" onClick={() => void save("supplied")}>تم التزويد</button>
              <button type="button" className="ghost-button" onClick={() => void save("delete")}>حذف</button>
              {item.isProcessed ? <button type="button" className="ghost-button" onClick={() => void save("clear")}>مسح القرار</button> : null}
            </div>
          ) : null}
        </div>
      ) : null}
    </article>
  );
}

function BranchCountsAndCorrection({ client, itemId, onMessage, onBusy, onConfirm }: PanelProps & { itemId: string }) {
  const run = useAction(onMessage, onBusy);
  const [branches, setBranches] = useState<ItemBranchState[] | null>(null);
  const [target, setTarget] = useState("");
  const [quantity, setQuantity] = useState("");
  const [reason, setReason] = useState("");
  const [requestId, setRequestId] = useState(() => newRequestId());

  const load = useCallback(async () => {
    try {
      setBranches((await client.itemBranches(itemId)).branches);
    } catch (error) {
      onMessage((error as Error).message);
    }
  }, [client, itemId, onMessage]);

  useEffect(() => { void load(); }, [load]);
  if (!branches) return null;

  function correct() {
    const branch = branches?.find((entry) => entry.branchId === target);
    const value = parseCountInput(quantity);
    if (!branch) { onMessage("اختر الفرع."); return; }
    if (value === null) { onMessage("أدخل الكمية الصحيحة."); return; }
    if (reason.trim().length < 3) { onMessage("يرجى كتابة سبب التصحيح."); return; }
    onConfirm({
      title: "تصحيح الكمية",
      message: branch.current
        ? `سيتغير رصيد هذا الفرع إلى ${formatQuantity(value)} فوراً. هل تريد المتابعة؟`
        : `سيُرسل طلب تصحيح إلى ${branch.branchName} ليصبح الرصيد ${formatQuantity(value)}، وينفّذه الفرع خلال دقيقة إذا كان متصلاً. هل تريد المتابعة؟`,
      accept: "تصحيح",
      onAccept: () => {
        void run("جاري التصحيح...", () => client.correct(itemId, branch.branchId, requestId, value, reason.trim())).then((result) => {
          if (!result) return;
          setQuantity("");
          setReason("");
          setRequestId(newRequestId());
          onMessage(result.pending ? "أُرسل طلب التصحيح. يظهر «قيد التنفيذ في الفرع» حتى ينفّذه." : "تم تصحيح الكمية.");
          void load();
        });
      },
    });
  }

  return (
    <div className="inventory-branch-counts">
      <table className="inventory-table">
        <thead><tr><th>الفرع</th><th>الرصيد على المركز</th><th>آخر جرد</th><th>تصحيح</th></tr></thead>
        <tbody>
          {branches.map((branch) => (
            <tr key={branch.branchId}>
              <td>{branch.branchName}{branch.current ? " (هذا الفرع)" : ""}</td>
              <td>{formatQuantity(branch.quantity)} <small>{formatDateTime(branch.snapshotAt)}</small></td>
              <td>{branch.lastCountedQuantity === null ? "—" : `${formatQuantity(branch.lastCountedQuantity)} · ${formatDateTime(branch.lastCountedAt)}`}</td>
              <td>{branch.pendingCorrection !== null ? `قيد التنفيذ في الفرع (${formatQuantity(branch.pendingCorrection)})` : "—"}</td>
            </tr>
          ))}
        </tbody>
      </table>
      <div className="inventory-decision">
        <label>الفرع
          <select value={target} onChange={(event) => setTarget(event.target.value)}>
            <option value="">اختر الفرع</option>
            {branches.map((branch) => <option key={branch.branchId} value={branch.branchId}>{branch.branchName}</option>)}
          </select>
        </label>
        <label>الكمية الصحيحة<input inputMode="decimal" value={quantity} onChange={(event) => { setQuantity(event.target.value); setRequestId(newRequestId()); }} /></label>
        <label>السبب<input value={reason} maxLength={500} onChange={(event) => { setReason(event.target.value); setRequestId(newRequestId()); }} /></label>
        <button type="button" className="ghost-button" onClick={correct}>تصحيح</button>
      </div>
    </div>
  );
}

// ======================================================================
// متابعة النواقص
// ======================================================================

type FollowTab = FollowUpTab | "Requests" | "Suppliers";

export function FollowUpPanel({ client, canApprove, onMessage, onBusy, onConfirm }: PanelProps & { canApprove: boolean }) {
  const run = useAction(onMessage, onBusy);
  const [tab, setTab] = useState<FollowTab>("Purchase");
  const [from, setFrom] = useState("");
  const [to, setTo] = useState("");
  const [view, setView] = useState<FollowUpView | null>(null);
  const [suppliers, setSuppliers] = useState<Supplier[]>([]);
  const [requests, setRequests] = useState<SupplyRequest[]>([]);
  const [requestStatus, setRequestStatus] = useState("Open");

  const loadSuppliers = useCallback(async () => {
    const value = await run("جاري تحميل التجار...", () => client.suppliers());
    if (value) setSuppliers(value.suppliers);
  }, [client, run]);

  const load = useCallback(async (next: FollowTab = tab) => {
    if (next === "Suppliers") { await loadSuppliers(); return; }
    if (next === "Requests") {
      const value = await run("جاري تحميل طلبات التزويد...", () => client.supplyRequests(requestStatus));
      if (value) setRequests(value.requests);
      return;
    }
    const value = await run("جاري التحميل...", () => client.tasks(next, from, to));
    if (value) {
      setView(value);
      if (!from) setFrom(value.from);
      if (!to) setTo(value.to);
    }
  }, [client, run, tab, from, to, requestStatus, loadSuppliers]);

  useEffect(() => { void load(tab); void loadSuppliers(); }, []); // eslint-disable-line react-hooks/exhaustive-deps

  function choose(next: FollowTab) {
    setTab(next);
    void load(next);
  }

  async function act(body: Parameters<ShortageClient["taskAction"]>[0]) {
    const done = await run("جاري التنفيذ...", () => client.taskAction(body));
    if (done) await load(tab);
  }

  const counts = view?.counts;
  const tabs: [FollowTab, string][] = [
    ["Purchase", `مطلوب شراء${counts ? ` (${counts.purchase})` : ""}`],
    ["Warehouse", `المخزن${counts ? ` (${counts.warehouse})` : ""}`],
    ["Balance", `مطلوب موازنة${counts ? ` (${counts.balance})` : ""}`],
    ["Delete", `مطلوب حذف${counts ? ` (${counts.delete})` : ""}`],
    ["Done", `تمت المعالجة${counts ? ` (${counts.done})` : ""}`],
    ["Requests", "طلبات التزويد"],
    ["Suppliers", "التجار"],
  ];

  return (
    <div className="inventory-report">
      <div className="inventory-tabs" role="tablist">
        {tabs.map(([value, label]) => (
          <button key={value} type="button" role="tab" aria-selected={tab === value} className={tab === value ? "is-active" : undefined} onClick={() => choose(value)}>{label}</button>
        ))}
      </div>

      {tab !== "Requests" && tab !== "Suppliers" ? (
        <DateRange from={from} to={to} onChange={(nextFrom, nextTo) => { setFrom(nextFrom); setTo(nextTo); }} onSubmit={() => void load(tab)} />
      ) : null}

      {tab === "Suppliers" ? <SuppliersView suppliers={suppliers} onSave={async (supplier) => {
        const value = await run("جاري الحفظ...", () => client.saveSupplier(supplier));
        if (value) setSuppliers(value.suppliers);
      }} /> : null}

      {tab === "Requests" ? (
        <SupplyRequestsView
          requests={requests}
          suppliers={suppliers}
          status={requestStatus}
          onStatus={(status) => { setRequestStatus(status); void run("جاري التحميل...", () => client.supplyRequests(status)).then((value) => { if (value) setRequests(value.requests); }); }}
          onAction={(requestId, action, supplierId) => {
            const label = action === "purchased" ? "تم الشراء" : action === "revert" ? "تراجع" : "إحالة لتاجر آخر";
            onConfirm({
              title: label,
              message: action === "revert" ? "ستعود الأصناف إلى «مطلوب شراء». هل تريد المتابعة؟" : `${label} لهذا الطلب؟`,
              accept: label,
              onAccept: () => { void run("جاري التنفيذ...", () => client.supplyAction(requestId, action, supplierId)).then(() => load("Requests")); },
            });
          }}
        />
      ) : null}

      {tab !== "Requests" && tab !== "Suppliers" ? (
        <div className="inventory-list">
          {view && view.rows.length === 0 ? <p className="inventory-hint">لا توجد أصناف في هذا التبويب للفترة المختارة.</p> : null}
          {view?.rows.map((row) => isPurchaseRow(row) ? (
            <PurchaseRowView key={`${row.itemId}-${row.supplyRequestId ?? ""}`} row={row} suppliers={suppliers.filter((supplier) => supplier.isActive)} onAct={act} onMessage={onMessage} />
          ) : (
            <article key={row.taskId} className="inventory-row">
              <div className="inventory-row-name">
                <span className="inventory-number">{row.itemNumber}</span>
                <strong>{row.itemName || "صنف بدون اسم"}</strong>
                <small>
                  {row.quantity !== null ? `الكمية ${formatQuantity(row.quantity)} · ` : ""}{formatDateTime(row.assignedAt)} · {row.assignedByName ?? "—"}
                  {row.note ? ` · ${row.note}` : ""}
                </small>
                {tab === "Done" ? (
                  <small>{taskTypeLabel(row.taskType)} · {row.status === "Cancelled" ? "أُلغي" : "تم"} · {row.completedByName ?? "—"} · {formatDateTime(row.completedAt)}
                    {row.approvalStatus === "Approved" ? " · اعتُمد الحذف" : row.approvalStatus === "Rejected" ? " · رُفض الحذف" : ""}</small>
                ) : null}
              </div>
              <div className="inventory-counted">
                {tab === "Warehouse" || tab === "Balance" ? (
                  <button type="button" className="primary-button" onClick={() => void act({ action: "done", taskId: row.taskId })}>
                    {tab === "Warehouse" ? "تم التزويد من المخزن" : "تمت الموازنة"}
                  </button>
                ) : null}
                {tab === "Delete" && canApprove ? (
                  <>
                    <button
                      type="button"
                      className="primary-button"
                      onClick={() => onConfirm({
                        title: "اعتماد الحذف",
                        message: `سيُوقف الصنف ${row.itemNumber} في كل الفروع (لا يُمحى، ويُعاد تفعيله من شاشة الأصناف). هل تريد المتابعة؟`,
                        accept: "اعتماد الحذف",
                        onAccept: () => { void act({ action: "approveDelete", taskId: row.taskId }); },
                      })}
                    >
                      اعتماد الحذف
                    </button>
                    <button type="button" className="ghost-button" onClick={() => void act({ action: "rejectDelete", taskId: row.taskId })}>رفض</button>
                  </>
                ) : null}
                {tab === "Delete" && !canApprove ? <span className="inventory-badge">بانتظار موافقة المدير</span> : null}
              </div>
            </article>
          ))}
        </div>
      ) : null}
    </div>
  );
}

function taskTypeLabel(type: string): string {
  return type === "Purchase" ? "شراء" : type === "Warehouse" ? "مخزن" : type === "Balance" ? "موازنة" : type === "Delete" ? "حذف" : type;
}

function PurchaseRowView({ row, suppliers, onAct, onMessage }: {
  row: PurchaseRow;
  suppliers: Supplier[];
  onAct: (body: Parameters<ShortageClient["taskAction"]>[0]) => Promise<void>;
  onMessage: (message: string) => void;
}) {
  const [moved, setMoved] = useState("");
  const [supplier, setSupplier] = useState("");
  return (
    <article className="inventory-row">
      <div className="inventory-row-name">
        <span className="inventory-number">{row.itemNumber}</span>
        <strong>{row.itemName || "صنف بدون اسم"}</strong>
        <small>مطلوب شراء {formatQuantity(row.quantity)} · منذ {formatDateTime(row.assignedAt)}</small>
        {row.supplyRequestId ? <small className="inventory-saved">طلب تزويد #{row.requestNumber} لدى {row.supplierName}</small> : null}
      </div>
      <div className="inventory-decision">
        <button type="button" className="primary-button" onClick={() => void onAct({ action: "purchased", itemId: row.itemId, supplyRequestId: row.supplyRequestId })}>تم الشراء</button>
        {!row.supplyRequestId ? (
          <>
            <label>للمخزن<input inputMode="decimal" value={moved} onChange={(event) => setMoved(event.target.value)} aria-label={`للمخزن ${row.itemNumber}`} /></label>
            <button
              type="button"
              className="ghost-button"
              onClick={() => {
                const value = parseCountInput(moved);
                if (!value) { onMessage("أدخل الكمية المحوّلة للمخزن."); return; }
                void onAct({ action: "toWarehouse", itemId: row.itemId, quantity: value }).then(() => setMoved(""));
              }}
            >
              تحويل للمخزن
            </button>
            <label>التاجر
              <select value={supplier} onChange={(event) => setSupplier(event.target.value)}>
                <option value="">اختر التاجر</option>
                {suppliers.map((entry) => <option key={entry.supplierId} value={entry.supplierId}>{entry.name}</option>)}
              </select>
            </label>
            <button
              type="button"
              className="ghost-button"
              disabled={!supplier}
              onClick={() => void onAct({ action: "refer", itemId: row.itemId, supplierId: supplier })}
            >
              إحالة لتاجر
            </button>
          </>
        ) : null}
      </div>
    </article>
  );
}

function SupplyRequestsView({ requests, suppliers, status, onStatus, onAction }: {
  requests: SupplyRequest[];
  suppliers: Supplier[];
  status: string;
  onStatus: (status: string) => void;
  onAction: (requestId: string, action: "purchased" | "revert" | "reassign", supplierId?: string) => void;
}) {
  const [reassign, setReassign] = useState<Record<string, string>>({});
  return (
    <div className="inventory-list">
      <div className="inventory-filter">
        <label>الحالة
          <select value={status} onChange={(event) => onStatus(event.target.value)}>
            <option value="Open">مفتوحة</option>
            <option value="Purchased">تم الشراء</option>
            <option value="Reverted">متراجع عنها</option>
          </select>
        </label>
      </div>
      {requests.length === 0 ? <p className="inventory-hint">لا توجد طلبات تزويد.</p> : null}
      {requests.map((request) => (
        <article key={request.requestId} className="inventory-row">
          <div className="inventory-row-name">
            <strong>طلب تزويد #{request.requestNumber} · {request.supplierName}</strong>
            <small>{formatDateTime(request.createdAt)} · {request.createdByName ?? "—"}{request.supplierPhone ? ` · ${request.supplierPhone}` : ""}</small>
            <ul className="inventory-history">
              {request.items.map((item) => <li key={item.itemId}>{item.itemNumber} {item.itemName ?? ""}: {formatQuantity(item.quantity)}</li>)}
            </ul>
          </div>
          <div className="inventory-decision">
            <button type="button" className="ghost-button" onClick={() => printSupplyRequest(request)}>طباعة</button>
            {request.status === "Open" ? (
              <>
                <button type="button" className="primary-button" onClick={() => onAction(request.requestId, "purchased")}>تم الشراء</button>
                <button type="button" className="ghost-button" onClick={() => onAction(request.requestId, "revert")}>تراجع / إعادة لمسؤول الشراء</button>
                <select value={reassign[request.requestId] ?? ""} onChange={(event) => setReassign((current) => ({ ...current, [request.requestId]: event.target.value }))} aria-label="تاجر آخر">
                  <option value="">تاجر آخر</option>
                  {suppliers.filter((supplier) => supplier.isActive && supplier.supplierId !== request.supplierId).map((supplier) => (
                    <option key={supplier.supplierId} value={supplier.supplierId}>{supplier.name}</option>
                  ))}
                </select>
                <button
                  type="button"
                  className="ghost-button"
                  disabled={!reassign[request.requestId]}
                  onClick={() => onAction(request.requestId, "reassign", reassign[request.requestId])}
                >
                  إحالة
                </button>
              </>
            ) : <span className="inventory-badge">{request.status === "Purchased" ? "تم الشراء" : "متراجع عنه"}</span>}
          </div>
        </article>
      ))}
    </div>
  );
}

/** طباعة طلب التزويد (أو حفظه PDF من نافذة الطباعة). */
function printSupplyRequest(request: SupplyRequest) {
  const entities: Record<string, string> = { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" };
  const escape = (text: string) => text.replace(/[&<>"]/g, (char) => entities[char] ?? char);
  const rows = request.items.map((item) => `<tr><td>${escape(item.itemNumber)}</td><td>${escape(item.itemName ?? "")}</td><td>${formatQuantity(item.quantity)}</td></tr>`).join("");
  const html = `<!doctype html><html dir="rtl" lang="ar"><head><meta charset="utf-8"><title>طلب تزويد ${request.requestNumber}</title>
    <style>body{font-family:Tahoma,Arial,sans-serif;padding:24px}table{width:100%;border-collapse:collapse}td,th{border:1px solid #999;padding:6px;text-align:right}</style></head>
    <body><h2>طلب تزويد #${request.requestNumber}</h2><p>التاجر: ${escape(request.supplierName)}${request.supplierPhone ? ` · ${escape(request.supplierPhone)}` : ""}</p>
    <p>التاريخ: ${formatDateTime(request.createdAt)}</p><table><thead><tr><th>رقم الصنف</th><th>الصنف</th><th>الكمية</th></tr></thead><tbody>${rows}</tbody></table></body></html>`;
  const frame = document.createElement("iframe");
  frame.style.position = "fixed";
  frame.style.width = "0";
  frame.style.height = "0";
  frame.style.border = "0";
  document.body.appendChild(frame);
  const target = frame.contentWindow;
  if (!target) { frame.remove(); return; }
  target.document.open();
  target.document.write(html);
  target.document.close();
  target.focus();
  target.print();
  window.setTimeout(() => frame.remove(), 1000);
}

function SuppliersView({ suppliers, onSave }: { suppliers: Supplier[]; onSave: (supplier: Partial<Supplier> & { name: string }) => Promise<void> }) {
  const [editing, setEditing] = useState<Partial<Supplier> & { name: string }>({ name: "", phone: "", notes: "", isActive: true });
  return (
    <div className="inventory-list">
      <section className="inventory-section" aria-label="تاجر">
        <strong>{editing.supplierId ? "تعديل تاجر" : "تاجر جديد"}</strong>
        <label>الاسم<input value={editing.name} onChange={(event) => setEditing({ ...editing, name: event.target.value })} aria-label="اسم التاجر" /></label>
        <label>الهاتف<input value={editing.phone ?? ""} onChange={(event) => setEditing({ ...editing, phone: event.target.value })} aria-label="هاتف التاجر" /></label>
        <label>ملاحظات<input value={editing.notes ?? ""} onChange={(event) => setEditing({ ...editing, notes: event.target.value })} /></label>
        <label className="inventory-pick"><input type="checkbox" checked={editing.isActive ?? true} onChange={(event) => setEditing({ ...editing, isActive: event.target.checked })} />نشط</label>
        <button type="button" className="primary-button" onClick={() => void onSave(editing).then(() => setEditing({ name: "", phone: "", notes: "", isActive: true }))}>حفظ التاجر</button>
        {editing.supplierId ? <button type="button" className="ghost-button" onClick={() => setEditing({ name: "", phone: "", notes: "", isActive: true })}>جديد</button> : null}
      </section>
      {suppliers.map((supplier) => (
        <button key={supplier.supplierId} type="button" className="inventory-report-row" onClick={() => setEditing({ ...supplier })}>
          <strong>{supplier.name}</strong>
          <span>{supplier.phone ?? "—"}</span>
          {!supplier.isActive ? <span className="inventory-badge">موقوف</span> : null}
        </button>
      ))}
    </div>
  );
}

// ======================================================================
// تحديد جرد (الجرد المخصص)
// ======================================================================

export function DefineInventoryPanel({ client, onMessage, onBusy }: PanelProps) {
  const run = useAction(onMessage, onBusy);
  const [list, setList] = useState<CustomInventory[]>([]);
  const [branches, setBranches] = useState<{ branchId: string; branchName: string; current: boolean }[]>([]);
  const [editing, setEditing] = useState<{ id: string | null; name: string; isActive: boolean; branchIds: string[]; codes: string }>({ id: null, name: "", isActive: true, branchIds: [], codes: "" });
  const [notice, setNotice] = useState("");

  useEffect(() => {
    void run("جاري التحميل...", () => client.customInventories()).then((value) => {
      if (!value) return;
      setList(value.customInventories);
      setBranches(value.branches);
    });
  }, [client, run]);

  async function save() {
    const codes = parseItemCodes(editing.codes);
    if (!editing.name.trim()) { onMessage("اسم الجرد المخصص مطلوب."); return; }
    if (editing.branchIds.length === 0) { onMessage("يرجى اختيار فرع واحد على الأقل."); return; }
    if (codes.length === 0) { onMessage("يرجى إضافة صنف واحد على الأقل."); return; }
    setNotice("");
    const value = await run("جاري الحفظ...", () => client.saveCustomInventory({
      customInventoryId: editing.id,
      name: editing.name.trim(),
      isActive: editing.isActive,
      branchIds: editing.branchIds,
      itemCodes: codes,
    }));
    if (!value) return;
    setList(value.customInventories);
    setEditing({ id: null, name: "", isActive: true, branchIds: [], codes: "" });
    setNotice("تم الحفظ. يظهر في «الجرد» على أجهزة الفروع المختارة خلال دقيقة.");
  }

  return (
    <div className="inventory-report">
      {notice ? <p className="inventory-notice" role="status">{notice}</p> : null}
      <section className="inventory-section" aria-label="جرد مخصص">
        <strong>{editing.id ? "تعديل جرد مخصص" : "جرد مخصص جديد"}</strong>
        <label>الاسم<input value={editing.name} onChange={(event) => setEditing({ ...editing, name: event.target.value })} aria-label="اسم الجرد المخصص" /></label>
        <label className="inventory-pick"><input type="checkbox" checked={editing.isActive} onChange={(event) => setEditing({ ...editing, isActive: event.target.checked })} />نشط</label>
        <div className="inventory-checklist">
          {branches.map((branch) => (
            <label key={branch.branchId} className="inventory-pick">
              <input
                type="checkbox"
                checked={editing.branchIds.includes(branch.branchId)}
                onChange={() => setEditing((current) => ({
                  ...current,
                  branchIds: current.branchIds.includes(branch.branchId) ? current.branchIds.filter((id) => id !== branch.branchId) : [...current.branchIds, branch.branchId],
                }))}
              />
              {branch.branchName}
            </label>
          ))}
        </div>
        <label className="inventory-wide">أرقام الأصناف (سطر أو فاصلة لكل صنف)
          <textarea rows={5} value={editing.codes} onChange={(event) => setEditing({ ...editing, codes: event.target.value })} aria-label="أرقام الأصناف" />
        </label>
        <small className="inventory-hint">{parseItemCodes(editing.codes).length} صنف. العبوة تتحول لصنف الوحدة الأساسية.</small>
        <button type="button" className="primary-button" onClick={() => void save()}>حفظ</button>
        {editing.id ? <button type="button" className="ghost-button" onClick={() => setEditing({ id: null, name: "", isActive: true, branchIds: [], codes: "" })}>جديد</button> : null}
      </section>
      <div className="inventory-list">
        {list.map((entry) => (
          <button
            key={entry.customInventoryId}
            type="button"
            className="inventory-report-row"
            onClick={() => setEditing({ id: entry.customInventoryId, name: entry.name, isActive: entry.isActive, branchIds: entry.branchIds, codes: entry.items.map((item) => item.itemNumber).join("\n") })}
          >
            <strong>{entry.name}</strong>
            <span>{entry.items.length} صنف · {entry.branchNames.join("، ")}</span>
            {!entry.isActive ? <span className="inventory-badge">موقوف</span> : null}
          </button>
        ))}
      </div>
    </div>
  );
}

// ======================================================================
// التدقيق (طلبات تأكيد الاستلام)
// ======================================================================

export function AuditRequestsPanel({ client, onMessage, onBusy, onConfirm }: PanelProps) {
  const run = useAction(onMessage, onBusy);
  const [branches, setBranches] = useState<{ branchId: string; branchName: string }[]>([]);
  const [users, setUsers] = useState<UserOption[]>([]);
  const [branchId, setBranchId] = useState("");
  const [recipient, setRecipient] = useState("");
  const [note, setNote] = useState("");
  const [lines, setLines] = useState<{ code: string; expected: string }[]>([{ code: "", expected: "" }]);
  const [requests, setRequests] = useState<AuditRequest[]>([]);
  const [from, setFrom] = useState("");
  const [to, setTo] = useState("");

  const loadRequests = useCallback(async () => {
    const value = await run("جاري التحميل...", () => client.audits(from, to));
    if (value) setRequests(value.requests);
  }, [client, run, from, to]);

  useEffect(() => {
    void run("جاري التحميل...", () => client.auditOptions("")).then((value) => { if (value) setBranches(value.branches); });
    void loadRequests();
  }, []); // eslint-disable-line react-hooks/exhaustive-deps

  async function chooseBranch(id: string) {
    setBranchId(id);
    setRecipient("");
    if (!id) { setUsers([]); return; }
    const value = await run("جاري تحميل الموظفين...", () => client.auditOptions(id));
    if (value) setUsers(value.users);
  }

  async function create() {
    const items = lines.filter((line) => line.code.trim()).map((line) => ({ code: line.code.trim(), expectedQuantity: parseCountInput(line.expected) }));
    if (!branchId || !recipient) { onMessage("اختر الفرع والموظف."); return; }
    if (items.length === 0 || items.length > 10) { onMessage("طلب التدقيق من صنف واحد إلى 10 أصناف."); return; }
    if (items.some((item) => item.expectedQuantity === null)) { onMessage("أدخل الكمية المتوقعة لكل صنف."); return; }
    const value = await run("جاري الإنشاء...", () => client.createAudit({ branchId, recipientUserId: recipient, note, items: items as { code: string; expectedQuantity: number }[] }));
    if (!value) return;
    setLines([{ code: "", expected: "" }]);
    setNote("");
    await loadRequests();
    onMessage("تم إنشاء طلب التدقيق. يظهر للموظف في «تقرير الكفاءة» ليرد عليه خلال 7 أيام.");
  }

  return (
    <div className="inventory-report">
      <section className="inventory-section" aria-label="طلب تدقيق جديد">
        <strong>طلب تأكيد استلام جديد</strong>
        <label>الفرع
          <select value={branchId} onChange={(event) => void chooseBranch(event.target.value)}>
            <option value="">اختر الفرع</option>
            {branches.map((branch) => <option key={branch.branchId} value={branch.branchId}>{branch.branchName}</option>)}
          </select>
        </label>
        <label>الموظف
          <select value={recipient} onChange={(event) => setRecipient(event.target.value)}>
            <option value="">اختر الموظف</option>
            {users.map((user) => <option key={user.userId} value={user.userId}>{user.displayName}</option>)}
          </select>
        </label>
        <label>ملاحظة<input value={note} maxLength={500} onChange={(event) => setNote(event.target.value)} /></label>
        {lines.map((line, index) => (
          <div key={index} className="inventory-decision">
            <label>رقم الصنف<input value={line.code} onChange={(event) => setLines((current) => current.map((entry, position) => position === index ? { ...entry, code: event.target.value } : entry))} aria-label={`رقم الصنف ${index + 1}`} /></label>
            <label>الكمية المتوقعة<input inputMode="decimal" value={line.expected} onChange={(event) => setLines((current) => current.map((entry, position) => position === index ? { ...entry, expected: event.target.value } : entry))} aria-label={`الكمية المتوقعة ${index + 1}`} /></label>
            {lines.length > 1 ? <button type="button" className="ghost-button" onClick={() => setLines((current) => current.filter((_, position) => position !== index))}>حذف</button> : null}
          </div>
        ))}
        {lines.length < 10 ? <button type="button" className="ghost-button" onClick={() => setLines((current) => [...current, { code: "", expected: "" }])}>إضافة صنف</button> : null}
        <button type="button" className="primary-button" onClick={() => void create()}>إنشاء الطلب</button>
        <small className="inventory-hint">رابط واتساب العام غير مفعّل بعد (يحتاج عنواناً عاماً على HTTPS)؛ الموظف يرد من داخل XtraPOS.</small>
      </section>

      <DateRange from={from} to={to} onChange={(nextFrom, nextTo) => { setFrom(nextFrom); setTo(nextTo); }} onSubmit={() => void loadRequests()} />
      <div className="inventory-list">
        {requests.map((request) => (
          <AuditRequestView
            key={request.requestId}
            request={request}
            onCancel={request.status === "Pending" ? () => onConfirm({
              title: "إلغاء طلب التدقيق",
              message: "سيُلغى الطلب ولن يظهر للموظف. هل تريد المتابعة؟",
              accept: "إلغاء الطلب",
              onAccept: () => { void run("جاري الإلغاء...", () => client.cancelAudit(request.requestId)).then(() => loadRequests()); },
            }) : undefined}
          />
        ))}
      </div>
    </div>
  );
}

function auditStatusLabel(request: AuditRequest): string {
  if (request.status === "Answered") return `تم الرد · ${request.score ?? 0} من 10`;
  if (request.status === "Cancelled") return "ملغى";
  return request.expired ? "انتهت صلاحيته" : "بانتظار الرد";
}

function AuditRequestView({ request, onCancel }: { request: AuditRequest; onCancel?: () => void }) {
  return (
    <article className={request.status === "Answered" && (request.score ?? 0) < 10 ? "inventory-row is-mismatch" : "inventory-row"}>
      <div className="inventory-row-name">
        <strong>{request.recipientName ?? "—"} · {request.branchName ?? "—"}</strong>
        <small>{formatDateTime(request.createdAt)} · {request.createdByName ?? "—"}{request.note ? ` · ${request.note}` : ""}</small>
        <ul className="inventory-history">
          {request.items.map((item) => (
            <li key={item.requestItemId}>
              {item.itemNumber} {item.itemName ?? ""}: المتوقع {formatQuantity(item.expectedQuantity)}
              {item.receivedQuantity !== null ? ` · المستلم ${formatQuantity(item.receivedQuantity)}` : ""}
            </li>
          ))}
        </ul>
      </div>
      <div className="inventory-counted">
        <span className="inventory-badge">{auditStatusLabel(request)}</span>
        {onCancel ? <button type="button" className="ghost-button" onClick={onCancel}>إلغاء</button> : null}
      </div>
    </article>
  );
}

// ======================================================================
// تقرير الكفاءة (والرد على طلبات التدقيق)
// ======================================================================

export function EfficiencyPanel({ client, onMessage, onBusy }: PanelProps) {
  const run = useAction(onMessage, onBusy);
  const [pending, setPending] = useState<AuditRequest[]>([]);
  const [answers, setAnswers] = useState<Record<string, string>>({});
  const [rows, setRows] = useState<EfficiencyRow[]>([]);
  const [ownOnly, setOwnOnly] = useState(true);
  const [from, setFrom] = useState("");
  const [to, setTo] = useState("");

  const load = useCallback(async () => {
    const mine = await run("جاري التحميل...", () => client.myAudits(from, to));
    if (mine) setPending(mine.requests.filter((request) => request.status === "Pending" && !request.expired));
    const report = await run("جاري التحميل...", () => client.efficiency(from, to));
    if (report) {
      setRows(report.employees);
      setOwnOnly(report.ownOnly);
      if (!from) setFrom(report.from);
      if (!to) setTo(report.to);
    }
  }, [client, run, from, to]);

  useEffect(() => { void load(); }, []); // eslint-disable-line react-hooks/exhaustive-deps

  async function answer(request: AuditRequest) {
    const items = request.items.map((item) => ({ requestItemId: item.requestItemId, receivedQuantity: parseCountInput(answers[item.requestItemId] ?? "") }));
    if (items.some((item) => item.receivedQuantity === null)) { onMessage("أدخل الكمية المستلمة لكل الأصناف."); return; }
    const value = await run("جاري الإرسال...", () => client.answerAudit(request.requestId, items as { requestItemId: string; receivedQuantity: number }[]));
    if (!value) return;
    await load();
    onMessage(`تم الرد. درجتك ${value.score} من 10.`);
  }

  return (
    <div className="inventory-report">
      {pending.length > 0 ? (
        <section className="inventory-section" aria-label="طلبات بانتظار ردك">
          <strong>طلبات تأكيد استلام بانتظار ردك</strong>
          {pending.map((request) => (
            <div key={request.requestId} className="inventory-audit-start">
              <span>{request.createdByName ?? "—"} · {formatDateTime(request.createdAt)}{request.note ? ` · ${request.note}` : ""}</span>
              {request.items.map((item) => (
                <label key={item.requestItemId}>{item.itemNumber} {item.itemName ?? ""}: الكمية المستلمة
                  <input
                    inputMode="decimal"
                    value={answers[item.requestItemId] ?? ""}
                    onChange={(event) => setAnswers((current) => ({ ...current, [item.requestItemId]: event.target.value }))}
                    aria-label={`الكمية المستلمة ${item.itemNumber}`}
                  />
                </label>
              ))}
              <button type="button" className="primary-button" onClick={() => void answer(request)}>إرسال الرد</button>
            </div>
          ))}
        </section>
      ) : null}

      <DateRange from={from} to={to} onChange={(nextFrom, nextTo) => { setFrom(nextFrom); setTo(nextTo); }} onSubmit={() => void load()} />
      {ownOnly ? <p className="inventory-hint">تعرض الشاشة تقاريرك فقط.</p> : null}
      <table className="inventory-table">
        <thead><tr><th>الموظف</th><th>الطلبات</th><th>تم الرد</th><th>بانتظار الرد</th><th>الدرجة من 10</th></tr></thead>
        <tbody>
          {rows.length === 0 ? <tr><td colSpan={5}>لا توجد طلبات تدقيق في هذه الفترة.</td></tr> : null}
          {rows.map((row) => (
            <tr key={row.userId}>
              <td>{row.userName ?? "—"}</td>
              <td>{row.requests}</td>
              <td>{row.answered}</td>
              <td>{row.pending}</td>
              <td>{row.averageScore === null ? "—" : formatQuantity(row.averageScore)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

// ======================================================================
// تقرير معالجة النواقص
// ======================================================================

export function ShortageReportPanel({ client, onMessage, onBusy }: PanelProps) {
  const run = useAction(onMessage, onBusy);
  const [report, setReport] = useState<ProcessingReport | null>(null);
  const [from, setFrom] = useState("");
  const [to, setTo] = useState("");

  const load = useCallback(async () => {
    const value = await run("جاري التحميل...", () => client.report(from, to));
    if (!value) return;
    setReport(value);
    if (!from) setFrom(value.from);
    if (!to) setTo(value.to);
  }, [client, run, from, to]);

  useEffect(() => { void load(); }, []); // eslint-disable-line react-hooks/exhaustive-deps

  return (
    <div className="inventory-report">
      <DateRange from={from} to={to} onChange={(nextFrom, nextTo) => { setFrom(nextFrom); setTo(nextTo); }} onSubmit={() => void load()} />
      <section className="inventory-section" aria-label="المعالجات">
        <strong>المعالجات</strong>
        <table className="inventory-table">
          <thead><tr><th>النوع</th><th>الفروع</th><th>المعالج</th><th>الحالة</th><th>الأصناف</th><th>المهام</th><th>التاريخ</th></tr></thead>
          <tbody>
            {(report?.runs ?? []).length === 0 ? <tr><td colSpan={7}>لا توجد معالجات في هذه الفترة.</td></tr> : null}
            {report?.runs.map((entry) => (
              <tr key={entry.runId}>
                <td>{entry.shortageTypeLabel} ({entry.supplyDays} يوم)</td>
                <td>{entry.branches ?? "—"}</td>
                <td>{entry.createdByName ?? "—"}</td>
                <td>{entry.status === "Completed" ? "مثبتة" : "مسودة"}</td>
                <td>{entry.processedCount} من {entry.itemCount}</td>
                <td>{entry.tasks}</td>
                <td>{formatDateTime(entry.completedAt ?? entry.createdAt)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </section>
      <section className="inventory-section" aria-label="مسؤول الشراء المتأخر">
        <strong>مسؤول الشراء المتأخر (أكثر من {report?.overdueDays ?? 3} أيام)</strong>
        <table className="inventory-table">
          <thead><tr><th>الصنف</th><th>الكمية</th><th>منذ</th><th>التاجر</th></tr></thead>
          <tbody>
            {(report?.overduePurchases ?? []).length === 0 ? <tr><td colSpan={4}>لا يوجد شراء متأخر.</td></tr> : null}
            {report?.overduePurchases.map((entry, index) => (
              <tr key={`${entry.itemNumber}-${index}`}>
                <td>{entry.itemNumber} {entry.itemName ?? ""}</td>
                <td>{formatQuantity(entry.quantity)}</td>
                <td>{formatDateTime(entry.assignedAt)} · {entry.assignedByName ?? "—"}</td>
                <td>{entry.supplierName ?? "—"}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </section>
    </div>
  );
}
