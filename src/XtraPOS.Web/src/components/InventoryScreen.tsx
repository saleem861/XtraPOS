import { useCallback, useEffect, useMemo, useRef, useState, type FormEvent } from "react";
import {
  createInventoryClient,
  errorStatus,
  type InventoryBranch,
  type InventoryConfiguration,
  type InventoryItem,
  type InventoryLookup,
  type InventorySession,
  type InventoryType,
} from "../api/inventory";
import { createInventoryReviewClient } from "../api/inventoryReview";
import { createShortageClient } from "../api/shortages";
import { usesDesktopCustomerKeyboard } from "../customers/newCustomerForm";
import {
  defaultPages,
  filterItems,
  formatDifference,
  formatQuantity,
  hasPermission,
  INVENTORY_TYPES,
  inventoryMenu,
  inventoryTypeLabel,
  parseCountInput,
  stepQuantity,
  togglePage,
  type CountTab,
} from "../inventory/inventoryRules";
import type { DeviceSession } from "../types/setup";
import { FullKeyboard } from "./FullKeyboard";
import { CountQuantityField } from "./CountQuantityField";
import { Pager, usePaging } from "./Pager";
import { distinctBy } from "../distinct";
import { InventoryReportPanel, ItemProcessingPanel, type BackHandler } from "./InventoryReviewPanels";
import {
  AuditRequestsPanel,
  DefineInventoryPanel,
  EfficiencyPanel,
  FollowUpPanel,
  ShortageProcessingPanel,
  ShortageReportPanel,
} from "./ShortagePanels";
import { KeyboardToggle, useFullKeyboardVisible } from "./keyboardVisibility";
import { ScreenHeader } from "./ScreenHeader";
import { Waiting } from "./Waiting";
import "./inventory.css";

/**
 * تسلسل النظام القديم (inventory.sharbelfashion.com) داخل هيدر XtraPOS:
 * الفرع ← «تم اختيار الفرع» (متابعة) ← نوع الجرد ←
 *   المهمة: «تم اختيار نوع الجرد» (متابعة / جديد / تغيير الفرع)؛
 *   الدوري: «اختيار الصفحات» وتحتها متابعة / جديد / تغيير الفرع؛
 *   اليدوي: جرد يدوي، أو الجرد المخصص ← «اختيار اسم الجرد» مع حالة كل فرع.
 */
type Step = "menu" | "branch" | "branchConfirmed" | "type" | "typeCard" | "pages" | "manual" | "custom" | "count" | "done"
  | "report" | "items" | "define" | "shortages" | "followUp" | "audit" | "efficiency" | "shortageReport";

/** بطاقات تفتح لوحة كاملة، وعنوان كل لوحة. */
const PANEL_TITLES: Partial<Record<Step, string>> = {
  report: "تقرير الجرد",
  items: "معالجة الأصناف",
  define: "تحديد جرد",
  shortages: "معالجة النواقص",
  followUp: "متابعة النواقص",
  audit: "التدقيق",
  efficiency: "تقرير الكفاءة",
  shortageReport: "تقرير معالجة النواقص",
};

interface Confirmation {
  title: string;
  message: string;
  accept: string;
  onAccept: () => void;
}

export function InventoryScreen({ session, permissions, onBack }: {
  session: DeviceSession;
  permissions: readonly string[];
  onBack: () => void;
}) {
  const [branch, setBranch] = useState<InventoryBranch | null>(null);
  const [branches, setBranches] = useState<InventoryBranch[] | null>(null);
  const [branchNotice, setBranchNotice] = useState("");
  const [centralMode, setCentralMode] = useState(false);
  const [countPending, setCountPending] = useState(0);
  const baseClient = useMemo(() => createInventoryClient(session), [session]);
  const client = useMemo(() => createInventoryClient(session, branch?.branchId ?? ""), [session, branch]);
  const reviewClient = useMemo(() => createInventoryReviewClient(session), [session]);
  const shortageClient = useMemo(() => createShortageClient(session), [session]);
  const panelBack = useRef<BackHandler | null>(null);
  const registerBack = useCallback((handler: BackHandler | null) => { panelBack.current = handler; }, []);
  const canReview = hasPermission(permissions, "Inventory.Review");
  const [step, setStep] = useState<Step>("menu");
  const [type, setType] = useState<InventoryType | null>(null);
  const [customId, setCustomId] = useState("");
  const [config, setConfig] = useState<InventoryConfiguration | null>(null);
  const [pages, setPages] = useState<number[]>([]);
  const [openSession, setOpenSession] = useState<InventorySession | null>(null);
  const [checkingOpen, setCheckingOpen] = useState(false);
  const [active, setActive] = useState<InventorySession | null>(null);
  const [finished, setFinished] = useState<{ session: InventorySession; posted: number; remote?: boolean } | null>(null);
  const [message, setMessage] = useState("");
  const [busy, setBusy] = useState("");
  const [confirmation, setConfirmation] = useState<Confirmation | null>(null);
  const customInventories = useMemo(() => distinctBy(config?.customInventories, (custom) => custom.name), [config]);
  const canFollowUp = hasPermission(permissions, "Shortages.FollowUp");
  const [openTasks, setOpenTasks] = useState<number | null>(null);

  // كما في النظام القديم: «متابعة النواقص (N)» بعدد المهام المفتوحة، يُحدَّث كلما عادت القائمة.
  useEffect(() => {
    if (step !== "menu" || !canFollowUp) return;
    let cancelled = false;
    void shortageClient.openTaskCount()
      .then((count) => { if (!cancelled) setOpenTasks(count); })
      .catch(() => { if (!cancelled) setOpenTasks(null); });
    return () => { cancelled = true; };
  }, [step, canFollowUp, shortageClient]);

  useEffect(() => {
    let cancelled = false;
    void client.configuration()
      .then((value) => { if (!cancelled) setConfig(value); })
      .catch((error: Error) => { if (!cancelled) setMessage(error.message); });
    return () => { cancelled = true; };
  }, [client]);

  /** الفروع للجرد: فرع الجهاز أولاً، ثم فروع المركز. بلا مركز يبقى فرع الجهاز وحده. */
  function openBranches() {
    setMessage("");
    setStep("branch");
    const fallback: InventoryBranch[] = [{ branchId: session.branchId, branchName: session.branchName, current: true, lastPushAt: null, stale: false }];
    void baseClient.branches()
      .then((value) => {
        const list = distinctBy(value?.branches?.length ? value.branches : fallback, (entry) => entry.branchName);
        setBranches(list);
        setCentralMode(Boolean(value?.central));
        setBranchNotice(value?.notice ?? "");
      })
      .catch(() => { setBranches(fallback); setBranchNotice("تعذر تحميل الفروع. يظهر فرع هذا الجهاز فقط."); });
  }

  function back() {
    setMessage("");
    if (step === "menu" || step === "done") { onBack(); return; }
    if (PANEL_TITLES[step]) {
      if (panelBack.current?.()) return;
      setStep("menu");
      return;
    }
    if (step === "branch") { setStep("menu"); return; }
    if (step === "branchConfirmed") { setStep("branch"); return; }
    if (step === "type") { setStep("branch"); setCustomId(""); return; }
    if (step === "typeCard" || step === "pages") { setOpenSession(null); setStep("type"); return; }
    if (step === "manual") { setStep("type"); return; }
    if (step === "custom") { setStep("manual"); return; }
    if (step === "count") {
      if (countPending > 0) {
        setMessage(`انتظر حتى يُحفظ كل العدّ على المركز (بقي ${countPending}).`);
        return;
      }
      setConfirmation({
        title: "حفظ وخروج",
        message: "هل تريد حفظ الجرد والخروج؟ يمكنك متابعة الجرد لاحقًا من حيث توقفت.",
        accept: "حفظ وخروج",
        onAccept: () => { setActive(null); setStep("type"); },
      });
    }
  }

  /** كما في النظام القديم: اختيار الفرع يفتح بطاقة «تم اختيار الفرع» وفيها «متابعة». */
  function chooseBranch(chosen: InventoryBranch) {
    setMessage("");
    if (branch?.branchId !== chosen.branchId) setConfig(null);
    setBranch(chosen);
    setStep("branchConfirmed");
  }

  /** المهمة: بطاقة النوع (متابعة / جديد). الدوري: الصفحات وتحتها متابعة / جديد. اليدوي: يدوي أو مخصص ويفتح. */
  function chooseType(next: InventoryType) {
    setType(next);
    setCustomId("");
    setMessage("");
    setOpenSession(null);
    if (next === "Periodic") {
      setPages(defaultPages(config?.defaultPage ?? null));
      setStep("pages");
      return;
    }
    setPages([]);
    if (next === "Manual") {
      setStep("manual");
      return;
    }
    setStep("typeCard");
  }

  // جرد اليوم غير المثبت لهذا النوع (وهذه الصفحات)، ليُفعَّل «متابعة».
  useEffect(() => {
    if (!type || (step !== "typeCard" && step !== "pages")) return;
    if (step === "pages" && pages.length === 0) { setOpenSession(null); return; }
    let cancelled = false;
    setCheckingOpen(true);
    void client.findOpen(type, step === "pages" ? pages : [], "")
      .then((result) => { if (!cancelled) setOpenSession(result.session ?? null); })
      .catch((error: Error) => { if (!cancelled) setMessage(error.message); })
      .finally(() => { if (!cancelled) setCheckingOpen(false); });
    return () => { cancelled = true; };
  }, [client, step, type, pages]);

  function chooseCustom(id: string) {
    setType("Custom");
    setCustomId(id);
    setMessage("");
    void start("Custom", [], false, id);
  }

  async function start(next: InventoryType, chosen: number[], replaceOpen: boolean, custom = customId) {
    setBusy("جاري بدء الجرد...");
    setMessage("");
    try {
      const result = await client.start(next, chosen, replaceOpen, next === "Custom" ? custom : "");
      setActive(result.session);
      setOpenSession(null);
      setStep("count");
    } catch (error) {
      setMessage((error as Error).message);
    } finally {
      setBusy("");
    }
  }

  function confirmNew() {
    if (!type) return;
    if (type === "Periodic" && pages.length === 0) { setMessage("يرجى اختيار صفحة واحدة على الأقل"); return; }
    if (!openSession) { void start(type, pages, false); return; }
    const label = type === "Important" ? "جرد أصناف مهمة" : type === "Periodic" ? "جرد دوري" : type === "Custom" ? "جرد مخصص" : "جرد يدوي";
    setConfirmation({
      title: "جرد جديد",
      message: `يوجد ${label} سابق غير مثبت${type === "Periodic" ? " لهذه الصفحات" : ""}. بدء جرد جديد سيؤدي إلى مسح بيانات الجرد غير المثبتة السابقة. هل تريد المتابعة؟`,
      accept: "بدء جرد جديد",
      onAccept: () => { void start(type, pages, true); },
    });
  }

  /** «متابعة» و«جديد» و«تغيير الفرع» كما في النظام القديم: تحت بطاقة النوع، وتحت الصفحات في الدوري. */
  function resumeActions() {
    return (
      <div className="inventory-resume">
        <button
          type="button"
          className="primary-button"
          disabled={Boolean(busy) || checkingOpen || !openSession}
          onClick={() => { if (type) void start(type, pages, false); }}
        >متابعة</button>
        <small className="inventory-hint">
          {checkingOpen
            ? "جاري التحقق من وجود جرد مفتوح..."
            : openSession
              ? `جرد مفتوح اليوم: تم جرد ${openSession.countedItems} من ${openSession.totalItems || openSession.countedItems} صنف`
              : "لا يوجد جرد مفتوح اليوم"}
        </small>
        <button type="button" className="primary-button inventory-new" disabled={Boolean(busy) || checkingOpen} onClick={confirmNew}>جديد</button>
        <button type="button" className="link-button" onClick={() => { setOpenSession(null); setStep("branch"); }}>تغيير الفرع</button>
      </div>
    );
  }

  const title = step === "menu" ? "الجرد"
    : step === "done" ? "تم تثبيت الجرد"
      : PANEL_TITLES[step] ?? (type ? inventoryTypeLabel(type) : "الجرد");

  return (
    <section className="new-customer sales-review inventory-screen" dir="rtl" aria-label="الجرد">
      <ScreenHeader title={title} context={branch && step !== "menu" && !PANEL_TITLES[step] ? branch.branchName : session.branchName} onBack={back} />
      <div className="inventory-body">
        {message ? <p className="form-error inventory-message" role="alert">{message}</p> : null}
        {step === "menu" ? (
          <div className="inventory-menu">
            <p className="inventory-hint">الفرع: {session.branchName}</p>
            {inventoryMenu(permissions).map((card) => (
              <button
                key={card.id}
                type="button"
                className="inventory-card"
                disabled={!card.available}
                onClick={() => {
                  setMessage("");
                  if (card.id === "count") openBranches();
                  else if (card.id in PANEL_TITLES) setStep(card.id as Step);
                }}
              >
                <strong>{card.id === "followUp" && openTasks !== null ? `${card.label} (${openTasks})` : card.label}</strong>
                {card.available ? null : <small>قريباً</small>}
              </button>
            ))}
          </div>
        ) : null}

        {step === "branch" ? (
          <div className="inventory-menu">
            <p className="inventory-hint">اختر الفرع</p>
            {branchNotice ? <p className="inventory-hint is-warning">{branchNotice}</p> : null}
            {branches === null ? <Waiting compact label="جاري تحميل الفروع..." /> : branches.map((entry) => {
              const selected = branch?.branchId === entry.branchId;
              return (
                <button
                  key={entry.branchId}
                  type="button"
                  className={selected ? "inventory-card is-selected" : "inventory-card"}
                  aria-pressed={selected}
                  disabled={Boolean(busy)}
                  onClick={() => chooseBranch(entry)}
                >
                  <strong>{entry.branchName}</strong>
                  <small>{entry.current ? "فرع هذا الجهاز" : "على المركز"}</small>
                </button>
              );
            })}
          </div>
        ) : null}

        {step === "branchConfirmed" && branch ? (
          <div className="inventory-confirm-card" role="group" aria-label="تم اختيار الفرع">
            <small>تم اختيار الفرع</small>
            <strong>{branch.branchName}</strong>
            {centralMode ? <small className="inventory-hint">الجرد يجري على المركز، ويُثبَّت الفارق عليه أولاً، ويأخذه الفرع في أول مزامنة.</small> : null}
            <button type="button" className="primary-button" onClick={() => { setMessage(""); setStep("type"); }}>متابعة</button>
          </div>
        ) : null}

        {step === "type" ? (
          <div className="inventory-menu">
            <p className="inventory-hint">الفرع: {branch?.branchName ?? session.branchName} · اختر نوع الجرد</p>
            {INVENTORY_TYPES.map((option) => (
              <button key={option.value} type="button" className="inventory-card" disabled={Boolean(busy)} onClick={() => chooseType(option.value)}>
                <strong>{option.label}</strong>
                {option.value === "Important" && config ? <small>{config.importantItems} صنف</small> : null}
              </button>
            ))}
          </div>
        ) : null}

        {step === "manual" ? (
          <div className="inventory-menu">
            <p className="inventory-hint">خيارات الجرد اليدوي</p>
            <button type="button" className="inventory-card" disabled={Boolean(busy)} onClick={() => { setType("Manual"); void start("Manual", [], false, ""); }}>
              <strong>جرد يدوي</strong>
              <small>أدخل الأصناف بنفسك</small>
            </button>
            <button type="button" className="inventory-card" disabled={Boolean(busy)} onClick={() => { setMessage(""); setStep("custom"); }}>
              <strong>الجرد المخصص</strong>
              <small>{customInventories.length} جرد محدد لهذا الفرع</small>
            </button>
          </div>
        ) : null}

        {step === "custom" ? (
          <div className="inventory-menu">
            <p className="inventory-hint">{customInventories.length > 0 ? "اختر اسم الجرد" : "لا يوجد جرد مخصص لهذا الفرع. يُحدَّد من «تحديد جرد»."}</p>
            {customInventories.map((custom) => (
              <button key={custom.customInventoryId} type="button" className="inventory-card" disabled={Boolean(busy)} onClick={() => chooseCustom(custom.customInventoryId)}>
                <strong>{custom.name}</strong>
                <small>{custom.items} صنف</small>
                {custom.branches?.length ? (
                  <span className="inventory-custom-branches">
                    الفروع: {distinctBy(custom.branches, (entry) => entry.branchName)
                      .map((entry) => `${entry.branchName} (${entry.completed ? "مكتمل" : "غير مكتمل"})`).join("، ")}
                  </span>
                ) : null}
              </button>
            ))}
          </div>
        ) : null}

        {step === "pages" && config ? (
          <div className="inventory-pages">
            <p className="inventory-hint">
              اختيار الصفحات. {config.defaultPage ? "صفحة اليوم محددة مسبقاً ويمكن تغييرها." : "لا توجد صفحة محددة لهذا اليوم."} يمكن اختيار صفحة واحدة أو أكثر.
            </p>
            <div className="inventory-page-grid">
              {config.pageItems.map((entry) => (
                <button
                  key={entry.page}
                  type="button"
                  className={pages.includes(entry.page) ? "inventory-page is-selected" : "inventory-page"}
                  aria-pressed={pages.includes(entry.page)}
                  onClick={() => setPages((current) => togglePage(current, entry.page))}
                >
                  <strong>{pages.includes(entry.page) ? "✓ " : ""}الصفحة {entry.page}</strong>
                  <small>{entry.items} صنف</small>
                </button>
              ))}
            </div>
            {resumeActions()}
          </div>
        ) : null}

        {step === "typeCard" && type ? (
          <div className="inventory-confirm-card" role="group" aria-label="تم اختيار نوع الجرد">
            <small>تم اختيار نوع الجرد</small>
            <strong>{inventoryTypeLabel(type)}</strong>
            <small>الفرع:</small>
            <strong>{branch?.branchName ?? session.branchName}</strong>
            {resumeActions()}
          </div>
        ) : null}

        {step === "count" && active ? (
          <CountingPanel
            client={client}
            session={active}
            canReview={canReview}
            onSession={setActive}
            onPendingChange={setCountPending}
            onMessage={setMessage}
            onConfirm={setConfirmation}
            onSaveAndExit={() => back()}
            onFinished={(result) => { setFinished(result); setActive(null); setStep("done"); }}
          />
        ) : null}

        {step === "report" ? (
          <InventoryReportPanel
            client={reviewClient}
            onMessage={setMessage}
            onBusy={setBusy}
            onConfirm={setConfirmation}
            onRegisterBack={registerBack}
          />
        ) : null}

        {step === "items" ? (
          <ItemProcessingPanel
            client={reviewClient}
            shortageClient={shortageClient}
            onRegisterBack={registerBack}
            canClassify={hasPermission(permissions, "Inventory.Classify")}
            canCorrect={hasPermission(permissions, "Inventory.CorrectQuantity")}
            onMessage={setMessage}
            onBusy={setBusy}
            onConfirm={setConfirmation}
          />
        ) : null}

        {step === "define" ? (
          <DefineInventoryPanel client={shortageClient} onMessage={setMessage} onBusy={setBusy} onConfirm={setConfirmation} onRegisterBack={registerBack} />
        ) : null}

        {step === "shortages" ? (
          <ShortageProcessingPanel client={shortageClient} onMessage={setMessage} onBusy={setBusy} onConfirm={setConfirmation} onRegisterBack={registerBack} />
        ) : null}

        {step === "followUp" ? (
          <FollowUpPanel
            client={shortageClient}
            canApprove={hasPermission(permissions, "Shortages.Approve")}
            onMessage={setMessage}
            onBusy={setBusy}
            onConfirm={setConfirmation}
          />
        ) : null}

        {step === "audit" ? (
          <AuditRequestsPanel client={shortageClient} onMessage={setMessage} onBusy={setBusy} onConfirm={setConfirmation} onRegisterBack={registerBack} />
        ) : null}

        {step === "efficiency" ? (
          <EfficiencyPanel client={shortageClient} onMessage={setMessage} onBusy={setBusy} onConfirm={setConfirmation} />
        ) : null}

        {step === "shortageReport" ? (
          <ShortageReportPanel client={shortageClient} onMessage={setMessage} onBusy={setBusy} onConfirm={setConfirmation} />
        ) : null}

        {step === "done" && finished ? (
          <div className="inventory-done">
            <strong>{finished.remote ? `تم تثبيت جرد ${branch?.branchName ?? "الفرع"} على المركز.` : "تم تثبيت الجرد وتحديث المخزون بنجاح."}</strong>
            <dl>
              <div><dt>إجمالي الأصناف</dt><dd>{finished.session.totalItems}</dd></div>
              <div><dt>تم جردها</dt><dd>{finished.session.countedItems}</dd></div>
              <div><dt>غير مجرودة</dt><dd>{finished.session.uncountedItems}</dd></div>
              <div><dt>{finished.remote ? "ثُبّتت على المركز" : "تم ترحيلها"}</dt><dd>{finished.posted}</dd></div>
            </dl>
            <p className="inventory-hint">
              {finished.remote
                ? "ثُبّت كل فارق على المركز أولاً، ويأخذه الفرع في أول مزامنة بإضافته إلى رصيده الحالي، فلا يضيع بيع حدث أثناء الجرد. الأصناف غير المجرودة لم تُغيَّر."
                : "تم تحديث كميات الأصناف المجرودة فقط. الأصناف غير المجرودة لم تُغيَّر كمياتها."}
            </p>
            <button type="button" className="primary-button" onClick={() => { setFinished(null); setStep("type"); }}>جرد آخر</button>
            <button type="button" className="ghost-button" onClick={onBack}>العودة إلى البيع</button>
          </div>
        ) : null}
      </div>

      {busy ? <div className="drawer-wait"><Waiting compact label={busy} /></div> : null}
      {confirmation ? (
        <div className="inventory-dialog" role="dialog" aria-modal="true" aria-label={confirmation.title}>
          <div className="inventory-dialog-card">
            <strong>{confirmation.title}</strong>
            <p>{confirmation.message}</p>
            <div className="inventory-dialog-actions">
              <button type="button" className="primary-button" onClick={() => { const action = confirmation.onAccept; setConfirmation(null); action(); }}>{confirmation.accept}</button>
              <button type="button" className="ghost-button" onClick={() => setConfirmation(null)}>إلغاء</button>
            </div>
          </div>
        </div>
      ) : null}
    </section>
  );
}

/** سطر حفظ ينتظر دوره في الطابور: الكمية، وحالة الصنف قبل الحفظ (للتراجع إن رفضه الخادم). */
interface PendingSave {
  itemId: string;
  itemNumber: string;
  quantity: number;
  previous: InventoryItem | null;
  attempts: number;
}

const wait = (milliseconds: number) => new Promise((resolve) => window.setTimeout(resolve, milliseconds));

/** الحفظ الذي لم يصل المركز يُحفظ على الجهاز، فلا يضيع إن أُعيد تحميل الصفحة، ويُرسل عند العودة للجرد. */
const PENDING_KEY = (sessionId: string) => `xtrapos.inventory.pending.${sessionId}`;
const BATCH_LIMIT = 100;

function readStoredPending(sessionId: string): PendingSave[] {
  try {
    const raw = window.localStorage.getItem(PENDING_KEY(sessionId));
    const parsed = raw ? (JSON.parse(raw) as PendingSave[]) : [];
    return Array.isArray(parsed) ? parsed.filter((job) => typeof job?.itemId === "string" && typeof job?.quantity === "number") : [];
  } catch {
    return [];
  }
}

function storePending(sessionId: string, jobs: PendingSave[]) {
  try {
    if (jobs.length === 0) window.localStorage.removeItem(PENDING_KEY(sessionId));
    else window.localStorage.setItem(PENDING_KEY(sessionId), JSON.stringify(jobs.map((job) => ({ ...job, attempts: 0 }))));
  } catch {
    // التخزين غير متاح: يبقى الطابور في الذاكرة فقط.
  }
}

function upsertItem(items: InventoryItem[], next: InventoryItem) {
  return items.some((item) => item.itemId === next.itemId)
    ? items.map((item) => (item.itemId === next.itemId ? next : item))
    : [...items, next];
}

function CountingPanel({ client, session, canReview, onSession, onPendingChange, onMessage, onConfirm, onSaveAndExit, onFinished }: {
  client: ReturnType<typeof createInventoryClient>;
  session: InventorySession;
  canReview: boolean;
  onSession: (session: InventorySession) => void;
  onPendingChange: (count: number) => void;
  onMessage: (message: string) => void;
  onConfirm: (confirmation: Confirmation) => void;
  onSaveAndExit: () => void;
  onFinished: (result: { session: InventorySession; posted: number; remote?: boolean }) => void;
}) {
  const manual = session.inventoryType === "Manual";
  const [items, setItems] = useState<InventoryItem[]>([]);
  const [loading, setLoading] = useState(true);
  const [tab, setTab] = useState<CountTab>("current");
  const [query, setQuery] = useState("");
  const [drafts, setDrafts] = useState<Record<string, string>>({});
  const [editing, setEditing] = useState<string | null>(null);
  const [lastSaved, setLastSaved] = useState<{ itemId: string; label: string } | null>(null);
  const [manualItem, setManualItem] = useState<InventoryLookup | null>(null);
  const [finalizing, setFinalizing] = useState(false);
  /** ملاحظة المالك: الحفظ في الخلفية. الصنف ينتقل فوراً، والطلبات تُرسل بالترتيب وتُعاد عند انقطاع المركز. */
  const queue = useRef<PendingSave[]>([]);
  const draining = useRef(false);
  const alive = useRef(true);
  const [pending, setPending] = useState<ReadonlyMap<string, number>>(() => new Map());
  const [retrying, setRetrying] = useState(false);
  const desktop = usesDesktopCustomerKeyboard();
  const [keyboardOpen] = useFullKeyboardVisible();

  useEffect(() => {
    alive.current = true;
    return () => { alive.current = false; };
  }, []);

  const pendingCount = useMemo(() => [...pending.values()].reduce((sum, count) => sum + count, 0), [pending]);
  useEffect(() => { onPendingChange(pendingCount); }, [pendingCount, onPendingChange]);
  useEffect(() => () => onPendingChange(0), [onPendingChange]);

  const load = useCallback(async () => {
    setLoading(true);
    try {
      const result = await client.items(session.sessionId);
      // عدّ لم يصل المركز في المرة السابقة (أُعيد تحميل الصفحة): يظهر محفوظاً ويُرسل الآن.
      const stored = queue.current.length === 0 ? readStoredPending(session.sessionId) : [];
      let loaded = result.items;
      for (const job of stored) {
        const base = loaded.find((item) => item.itemId === job.itemId) ?? job.previous;
        if (!base) continue;
        loaded = upsertItem(loaded, { ...base, isCounted: true, countedQuantity: job.quantity });
      }
      setItems(loaded);
      if (stored.length > 0) {
        queue.current.push(...stored.map((job) => ({ ...job, attempts: 0 })));
        setPending(() => {
          const next = new Map<string, number>();
          for (const job of queue.current) next.set(job.itemId, (next.get(job.itemId) ?? 0) + 1);
          return next;
        });
        void drain();
      }
      onSession(result.session);
    } catch (error) {
      onMessage((error as Error).message);
    } finally {
      setLoading(false);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [client, session.sessionId]);

  useEffect(() => { void load(); }, [load]);

  // ملاحظة المالك: بعد الحفظ ينتقل الصنف إلى «تم الجرد» ولا يبقى في «لم يتم الجرد».
  const visible = useMemo(() => filterItems(items, tab, manual && tab === "current" ? "" : query), [items, tab, query, manual]);
  const paging = usePaging(visible, `${tab}|${query}`);

  function markPending(itemId: string, delta: number) {
    setPending((current) => {
      const next = new Map(current);
      const count = (next.get(itemId) ?? 0) + delta;
      if (count > 0) next.set(itemId, count);
      else next.delete(itemId);
      return next;
    });
  }

  /**
   * يرسل كل ما في الطابور دفعة واحدة (حتى 100 صنف) بطلب واحد، ثم ما تجمّع بعدها.
   * انقطاع أو مهلة أو خطأ في المركز (5xx): يُعاد تلقائياً. صنف رفضه الخادم: يرجع كما كان مع رسالة.
   */
  async function drain() {
    if (draining.current) return;
    draining.current = true;
    let attempts = 0;
    try {
      while (alive.current && queue.current.length > 0) {
        const batch = queue.current.slice(0, BATCH_LIMIT);
        const latest = new Map<string, number>();
        for (const job of batch) latest.set(job.itemId, job.quantity);
        try {
          const result = await client.countBatch(
            session.sessionId,
            [...latest.entries()].map(([itemId, quantity]) => ({ itemId, quantity })),
          );
          queue.current.splice(0, batch.length);
          storePending(session.sessionId, queue.current);
          attempts = 0;
          setRetrying(false);
          const stillQueued = new Set(queue.current.map((job) => job.itemId));
          const saved = result.items.filter((item) => !stillQueued.has(item.itemId));
          const rejected = new Map(result.rejected.map((entry) => [entry.itemId, entry.message]));
          setItems((current) => {
            let next = current;
            for (const item of saved) next = upsertItem(next, item);
            for (const job of batch) {
              if (!rejected.has(job.itemId) || stillQueued.has(job.itemId)) continue;
              next = job.previous ? upsertItem(next, job.previous) : next.filter((item) => item.itemId !== job.itemId);
            }
            return next;
          });
          if (rejected.size > 0) {
            const numbers = batch.filter((job) => rejected.has(job.itemId)).map((job) => job.itemNumber);
            onMessage(`لم يُحفظ عدّ ${[...new Set(numbers)].join("، ")}: ${[...rejected.values()][0]}`);
          }
          onSession(result.session);
          for (const job of batch) markPending(job.itemId, -1);
        } catch (error) {
          const status = errorStatus(error);
          if (status !== undefined && status >= 400 && status < 500 && status !== 408 && status !== 429) {
            // الدفعة كلها مرفوضة (الجرد أُغلق مثلاً): لا تُعاد، وترجع الأصناف كما كانت.
            queue.current.splice(0, batch.length);
            storePending(session.sessionId, queue.current);
            const stillQueued = new Set(queue.current.map((job) => job.itemId));
            setItems((current) => batch.reduce(
              (next, job) => stillQueued.has(job.itemId)
                ? next
                : job.previous ? upsertItem(next, job.previous) : next.filter((item) => item.itemId !== job.itemId),
              current,
            ));
            for (const job of batch) markPending(job.itemId, -1);
            onMessage(`لم يُحفظ العدّ: ${(error as Error).message}`);
            continue;
          }
          attempts += 1;
          setRetrying(true);
          await wait(Math.min(15000, 1000 * 2 ** Math.min(attempts, 4)));
        }
      }
    } finally {
      draining.current = false;
    }
  }

  function save(itemId: string) {
    // الكمية الافتراضية صفر (باهت): الحفظ بلا كتابة يحفظ صفراً.
    const typed = (drafts[itemId] ?? "").trim();
    const quantity = parseCountInput(typed === "" ? "0" : typed);
    if (quantity === null) {
      onMessage("أدخل كمية صحيحة. الصفر مسموح والسالبة غير مسموحة.");
      return;
    }

    const previous = items.find((item) => item.itemId === itemId) ?? null;
    const base: InventoryItem | null = previous ?? (manualItem?.itemId === itemId
      ? {
        sessionItemId: "", itemId, itemNumber: manualItem.itemNumber, itemName: manualItem.itemName, isCounted: false,
        countedQuantity: null, countedAt: null, countedByName: null, posted: false, expectedQuantity: null, difference: null,
        quantityAfterPosting: null,
      }
      : null);
    if (!base) return;

    onMessage("");
    const optimistic: InventoryItem = {
      ...base,
      isCounted: true,
      countedQuantity: quantity,
      countedAt: new Date().toISOString(),
      difference: base.expectedQuantity === null ? null : quantity - base.expectedQuantity,
    };
    setItems((current) => upsertItem(current, optimistic));
    setDrafts((current) => { const next = { ...current }; delete next[itemId]; return next; });
    setEditing(null);
    setLastSaved({ itemId, label: base.itemName || base.itemNumber });
    if (manual) setManualItem(null);

    queue.current.push({ itemId, itemNumber: base.itemNumber, quantity, previous, attempts: 0 });
    storePending(session.sessionId, queue.current);
    markPending(itemId, 1);
    void drain();

    // الصنف التالي في «لم يتم الجرد» يأخذ التركيز مباشرة.
    if (!manual && tab === "current") {
      const next = visible.find((item) => item.itemId !== itemId);
      if (next) window.setTimeout(() => document.querySelector<HTMLInputElement>(`[data-count-item='${next.itemId}']`)?.focus(), 0);
    }
  }

  async function lookup(event: FormEvent) {
    event.preventDefault();
    const code = query.trim();
    if (!code) return;
    onMessage("");
    try {
      const found = await client.lookup(session.sessionId, code);
      // الحالة المحلية أحدث من الخادم ما دام في الطابور حفظ لم يصل بعد.
      const local = items.find((entry) => entry.itemId === found.itemId);
      const isCounted = local?.isCounted ?? found.isCounted;
      const countedQuantity = local?.isCounted ? local.countedQuantity : found.countedQuantity;
      if (manual) {
        setManualItem(found);
        setTab("current");
        setDrafts((current) => ({ ...current, [found.itemId]: countedQuantity === null ? "" : String(countedQuantity) }));
        if (isCounted) onMessage("تم جرد هذا الصنف سابقًا، ويمكنك تعديل الكمية.");
        setQuery("");
      } else {
        setTab(isCounted ? "counted" : "current");
        setQuery(found.itemNumber);
        if (isCounted) {
          setEditing(found.itemId);
          setDrafts((current) => ({ ...current, [found.itemId]: formatQuantity(countedQuantity) }));
        }
      }
      window.setTimeout(() => document.querySelector<HTMLInputElement>(`[data-count-item='${found.itemId}']`)?.focus(), 0);
    } catch (error) {
      onMessage((error as Error).message);
    }
  }

  function finalize() {
    if (pendingCount > 0) {
      onMessage(`انتظر حتى يُحفظ كل العدّ على المركز (بقي ${pendingCount}).`);
      return;
    }
    onConfirm({
      title: "تثبيت الجرد",
      message: "سيتم تثبيت الجرد وترحيل الفروقات إلى مخزون الفرع. هل تريد المتابعة؟",
      accept: "تثبيت",
      onAccept: () => {
        void (async () => {
          setFinalizing(true);
          onMessage("");
          try {
            onFinished(await client.finalize(session.sessionId));
          } catch (error) {
            onMessage((error as Error).message);
          } finally {
            setFinalizing(false);
          }
        })();
      },
    });
  }

  function editor(itemId: string, label: string) {
    const value = drafts[itemId] ?? "";
    return (
      <div className="inventory-qty">
        <button type="button" aria-label="إنقاص الكمية" onClick={() => setDrafts((current) => ({ ...current, [itemId]: stepQuantity(current[itemId] ?? "", -1) }))}>−</button>
        <CountQuantityField
          data-count-item={itemId}
          label={`الكمية الفعلية ${label}`}
          title={label}
          value={value}
          onChange={(next) => setDrafts((current) => ({ ...current, [itemId]: next }))}
          onEnter={() => save(itemId)}
        />
        <button type="button" aria-label="زيادة الكمية" onClick={() => setDrafts((current) => ({ ...current, [itemId]: stepQuantity(current[itemId] ?? "", 1) }))}>+</button>
        <button type="button" className="primary-button" onClick={() => save(itemId)}>حفظ</button>
      </div>
    );
  }

  /**
   * قاعدة المالك: «تثبيت الجرد» يُفعَّل بعد حفظ عدّ كل الأصناف (في اليدوي كل ما أُضيف)، وبعد أن يصل كل حفظ للمركز.
   * العدّاد من الأصناف في الشاشة، فيتحرك فوراً مع الحفظ في الخلفية.
   */
  const counted = items.filter((item) => item.isCounted).length;
  const total = manual ? counted : session.totalItems;
  const remaining = manual ? 0 : Math.max(total - counted, 0);
  const allCounted = pendingCount === 0 && (manual
    ? counted > 0 && !manualItem
    : total > 0 && remaining === 0);

  const progress = manual
    ? `تم جرد ${counted} صنف`
    : `تم جرد ${counted} من ${total} صنف`;

  return (
    <div className="inventory-count">
      <header className="inventory-count-head">
        <div>
          <strong>{session.inventoryTypeLabel}</strong>
          {session.pages.length > 0 ? <span>الصفحات: {session.pages.join("، ")}</span> : null}
        </div>
        <span className="inventory-progress">{progress}</span>
      </header>

      <form className="inventory-search" onSubmit={(event) => void lookup(event)}>
        <input
          value={query}
          onChange={(event) => setQuery(event.target.value)}
          placeholder={manual ? "رقم الصنف / باركود" : "بحث باسم الصنف أو رقم الصنف"}
          aria-label="بحث"
        />
        {query ? <button type="button" className="ghost-button" aria-label="مسح البحث" onClick={() => setQuery("")}>×</button> : null}
        <button type="submit" className="ghost-button">{manual ? "إضافة" : "بحث"}</button>
      </form>

      <div className="inventory-tabs" role="tablist">
        <button type="button" role="tab" aria-selected={tab === "current"} className={tab === "current" ? "is-active" : undefined} onClick={() => setTab("current")}>
          {manual ? "الجرد الحالي" : `لم يتم الجرد (${remaining})`}
        </button>
        <button type="button" role="tab" aria-selected={tab === "counted"} className={tab === "counted" ? "is-active" : undefined} onClick={() => setTab("counted")}>
          تم الجرد ({counted})
        </button>
      </div>

      {lastSaved ? (
        <p className="inventory-hint inventory-last-saved" role="status">
          تم حفظ عدّ «{lastSaved.label}»{pending.has(lastSaved.itemId) ? " · يُرسل للمركز..." : ""}
        </p>
      ) : null}
      {pendingCount > 0 ? (
        <p className={retrying ? "inventory-hint is-warning" : "inventory-hint"} role="status">
          {retrying
            ? `تعذر الوصول للمركز. سيُعاد حفظ ${pendingCount} صنف تلقائياً، لا تغلق الشاشة.`
            : `جاري إرسال ${pendingCount} صنف للمركز...`}
        </p>
      ) : null}

      <div className="inventory-list">
        {loading ? <Waiting compact label="جاري تحميل الأصناف..." /> : null}
        {!loading && manual && tab === "current" ? (
          manualItem ? (
            <article className="inventory-row">
              <div className="inventory-row-name">
                <span className="inventory-number">{manualItem.itemNumber}</span>
                <strong>{manualItem.itemName}</strong>
                {manualItem.convertedFromPackage ? <small>تم تحويل {manualItem.scannedItemNumber} إلى وحدة الجرد الأساسية.</small> : null}
              </div>
              {editor(manualItem.itemId, manualItem.itemName)}
            </article>
          ) : <p className="inventory-hint">اكتب رقم الصنف أو امسح الباركود ثم اضغط «إضافة».</p>
        ) : null}
        {!loading && !(manual && tab === "current") ? paging.visible.map((item) => (
          <article
            key={item.itemId}
            className={["inventory-row", item.isCounted ? "is-selected" : "", lastSaved?.itemId === item.itemId ? "is-saved" : ""].filter(Boolean).join(" ")}
            aria-label={item.isCounted ? `${item.itemNumber} تم حفظ العد` : undefined}
          >
            <div className="inventory-row-name">
              <span className="inventory-number">{item.itemNumber}</span>
              <strong>{item.itemName || "صنف بدون اسم"}</strong>
            </div>
            {item.isCounted && editing !== item.itemId ? (
              <div className="inventory-counted">
                {pending.has(item.itemId)
                  ? <span className="inventory-saved is-pending">يُرسل للمركز...</span>
                  : lastSaved?.itemId === item.itemId ? <span className="inventory-saved">تم حفظ العد</span> : null}
                <span>المعدود: {formatQuantity(item.countedQuantity)}</span>
                {canReview ? <span>المفترض: {formatQuantity(item.expectedQuantity)}</span> : null}
                {canReview ? <span>الفارق: {formatDifference(item.difference)}</span> : null}
                <button type="button" className="ghost-button" onClick={() => { setEditing(item.itemId); setDrafts((current) => ({ ...current, [item.itemId]: formatQuantity(item.countedQuantity) })); }}>تعديل</button>
              </div>
            ) : (
              <>
                {editor(item.itemId, item.itemName ?? item.itemNumber)}
                {editing === item.itemId ? <button type="button" className="ghost-button" onClick={() => setEditing(null)}>إلغاء</button> : null}
              </>
            )}
          </article>
        )) : null}
        {!loading && !(manual && tab === "current") && visible.length === 0 ? (
          <p className="inventory-hint">{tab === "counted" ? "لم يتم جرد أي صنف بعد." : "لا توجد أصناف مطابقة للبحث في الجرد الحالي."}</p>
        ) : null}
        {!loading && !(manual && tab === "current") ? <Pager {...paging} /> : null}
      </div>

      <footer className="inventory-actions">
        {!allCounted ? (
          <small className="inventory-hint">
            {pendingCount > 0
              ? `يُفعَّل «تثبيت الجرد» بعد وصول كل الحفظ للمركز (بقي ${pendingCount}).`
              : manual ? "احفظ عدّ الأصناف المضافة لتفعيل «تثبيت الجرد»." : `احفظ عدّ كل الأصناف لتفعيل «تثبيت الجرد» (بقي ${remaining}).`}
          </small>
        ) : null}
        <button type="button" className="ghost-button" disabled={finalizing || pendingCount > 0} onClick={onSaveAndExit}>حفظ وخروج</button>
        <button type="button" className="primary-button" disabled={finalizing || !allCounted} onClick={finalize}>
          {finalizing ? "جاري التثبيت..." : "تثبيت الجرد"}
        </button>
      </footer>
      {desktop ? <KeyboardToggle /> : null}
      {desktop && keyboardOpen ? <FullKeyboard onEnter={() => (document.activeElement as HTMLElement | null)?.dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", bubbles: true }))} /> : null}
    </div>
  );
}
