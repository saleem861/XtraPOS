import { useCallback, useEffect, useMemo, useRef, useState, type FormEvent } from "react";
import {
  createInventoryClient,
  type InventoryConfiguration,
  type InventoryItem,
  type InventoryLookup,
  type InventorySession,
  type InventoryType,
} from "../api/inventory";
import { createInventoryReviewClient } from "../api/inventoryReview";
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
import { InventoryReportPanel, ItemProcessingPanel, type BackHandler } from "./InventoryReviewPanels";
import { KeyboardToggle, useFullKeyboardVisible } from "./keyboardVisibility";
import { ScreenHeader } from "./ScreenHeader";
import { Waiting } from "./Waiting";
import "./inventory.css";

type Step = "menu" | "type" | "pages" | "resume" | "count" | "done" | "report" | "items";

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
  const client = useMemo(() => createInventoryClient(session), [session]);
  const reviewClient = useMemo(() => createInventoryReviewClient(session), [session]);
  const panelBack = useRef<BackHandler | null>(null);
  const registerBack = useCallback((handler: BackHandler | null) => { panelBack.current = handler; }, []);
  const canReview = hasPermission(permissions, "Inventory.Review");
  const [step, setStep] = useState<Step>("menu");
  const [type, setType] = useState<InventoryType | null>(null);
  const [config, setConfig] = useState<InventoryConfiguration | null>(null);
  const [pages, setPages] = useState<number[]>([]);
  const [openSession, setOpenSession] = useState<InventorySession | null>(null);
  const [active, setActive] = useState<InventorySession | null>(null);
  const [finished, setFinished] = useState<{ session: InventorySession; posted: number } | null>(null);
  const [message, setMessage] = useState("");
  const [busy, setBusy] = useState("");
  const [confirmation, setConfirmation] = useState<Confirmation | null>(null);

  useEffect(() => {
    let cancelled = false;
    void client.configuration()
      .then((value) => { if (!cancelled) setConfig(value); })
      .catch((error: Error) => { if (!cancelled) setMessage(error.message); });
    return () => { cancelled = true; };
  }, [client]);

  function back() {
    setMessage("");
    if (step === "menu" || step === "done") { onBack(); return; }
    if (step === "report" || step === "items") {
      if (panelBack.current?.()) return;
      setStep("menu");
      return;
    }
    if (step === "type") { setStep("menu"); return; }
    if (step === "pages") { setStep("type"); return; }
    if (step === "resume") { setStep(type === "Periodic" ? "pages" : "type"); return; }
    if (step === "count") {
      setConfirmation({
        title: "حفظ وخروج",
        message: "هل تريد حفظ الجرد والخروج؟ يمكنك متابعة الجرد لاحقًا من حيث توقفت.",
        accept: "حفظ وخروج",
        onAccept: () => { setActive(null); setStep("type"); },
      });
    }
  }

  async function chooseType(next: InventoryType) {
    setType(next);
    setMessage("");
    if (next === "Periodic") {
      setPages(defaultPages(config?.defaultPage ?? null));
      setStep("pages");
      return;
    }
    await checkOpen(next, []);
  }

  async function checkOpen(next: InventoryType, chosen: number[]) {
    setBusy("جاري التحقق من وجود جرد مفتوح...");
    try {
      const result = await client.findOpen(next, chosen);
      if (result.session) {
        setOpenSession(result.session);
        setStep("resume");
      } else {
        await start(next, chosen, false);
      }
    } catch (error) {
      setMessage((error as Error).message);
    } finally {
      setBusy("");
    }
  }

  async function start(next: InventoryType, chosen: number[], replaceOpen: boolean) {
    setBusy("جاري بدء الجرد...");
    setMessage("");
    try {
      const result = await client.start(next, chosen, replaceOpen);
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
    const label = type === "Important" ? "جرد أصناف مهمة" : type === "Periodic" ? "جرد دوري" : "جرد يدوي";
    setConfirmation({
      title: "جرد جديد",
      message: `يوجد ${label} سابق غير مثبت${type === "Periodic" ? " لهذه الصفحات" : ""}. بدء جرد جديد سيؤدي إلى مسح بيانات الجرد غير المثبتة السابقة. هل تريد المتابعة؟`,
      accept: "بدء جرد جديد",
      onAccept: () => { void start(type, pages, true); },
    });
  }

  const title = step === "menu" ? "الجرد"
    : step === "done" ? "تم تثبيت الجرد"
      : step === "report" ? "تقرير الجرد"
      : step === "items" ? "معالجة الأصناف"
      : type ? inventoryTypeLabel(type) : "الجرد";

  return (
    <section className="new-customer sales-review inventory-screen" dir="rtl" aria-label="الجرد">
      <ScreenHeader title={title} context={session.branchName} onBack={back} />
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
                  if (card.id === "count") setStep("type");
                  if (card.id === "report") setStep("report");
                  if (card.id === "items") setStep("items");
                }}
              >
                <strong>{card.label}</strong>
                {card.available ? null : <small>قريباً</small>}
              </button>
            ))}
          </div>
        ) : null}

        {step === "type" ? (
          <div className="inventory-menu">
            <p className="inventory-hint">اختر نوع الجرد</p>
            {INVENTORY_TYPES.map((option) => (
              <button key={option.value} type="button" className="inventory-card" disabled={Boolean(busy)} onClick={() => void chooseType(option.value)}>
                <strong>{option.label}</strong>
                {option.value === "Important" && config ? <small>{config.importantItems} صنف</small> : null}
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
                  <strong>الصفحة {entry.page}</strong>
                  <small>{entry.items} صنف</small>
                </button>
              ))}
            </div>
            <button
              type="button"
              className="primary-button"
              disabled={Boolean(busy)}
              onClick={() => {
                if (pages.length === 0) { setMessage("يرجى اختيار صفحة واحدة على الأقل"); return; }
                void checkOpen("Periodic", pages);
              }}
            >متابعة</button>
          </div>
        ) : null}

        {step === "resume" && openSession && type ? (
          <div className="inventory-menu">
            <p className="inventory-hint">
              يوجد جرد غير مثبت من اليوم: تم جرد {openSession.countedItems} من {openSession.totalItems || openSession.countedItems} صنف.
            </p>
            <button type="button" className="inventory-card" disabled={Boolean(busy)} onClick={() => void start(type, pages, false)}>
              <strong>متابعة</strong>
              <small>من حيث توقفت</small>
            </button>
            <button type="button" className="inventory-card" disabled={Boolean(busy)} onClick={confirmNew}>
              <strong>جديد</strong>
              <small>يمسح الجرد غير المثبت</small>
            </button>
          </div>
        ) : null}

        {step === "count" && active ? (
          <CountingPanel
            client={client}
            session={active}
            canReview={canReview}
            onSession={setActive}
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
            canClassify={hasPermission(permissions, "Inventory.Classify")}
            canCorrect={hasPermission(permissions, "Inventory.CorrectQuantity")}
            onMessage={setMessage}
            onBusy={setBusy}
            onConfirm={setConfirmation}
          />
        ) : null}

        {step === "done" && finished ? (
          <div className="inventory-done">
            <strong>تم تثبيت الجرد وتحديث المخزون بنجاح.</strong>
            <dl>
              <div><dt>إجمالي الأصناف</dt><dd>{finished.session.totalItems}</dd></div>
              <div><dt>تم جردها</dt><dd>{finished.session.countedItems}</dd></div>
              <div><dt>غير مجرودة</dt><dd>{finished.session.uncountedItems}</dd></div>
              <div><dt>تم ترحيلها</dt><dd>{finished.posted}</dd></div>
            </dl>
            <p className="inventory-hint">تم تحديث كميات الأصناف المجرودة فقط. الأصناف غير المجرودة لم تُغيَّر كمياتها.</p>
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

function CountingPanel({ client, session, canReview, onSession, onMessage, onConfirm, onSaveAndExit, onFinished }: {
  client: ReturnType<typeof createInventoryClient>;
  session: InventorySession;
  canReview: boolean;
  onSession: (session: InventorySession) => void;
  onMessage: (message: string) => void;
  onConfirm: (confirmation: Confirmation) => void;
  onSaveAndExit: () => void;
  onFinished: (result: { session: InventorySession; posted: number }) => void;
}) {
  const manual = session.inventoryType === "Manual";
  const [items, setItems] = useState<InventoryItem[]>([]);
  const [loading, setLoading] = useState(true);
  const [tab, setTab] = useState<CountTab>("current");
  const [query, setQuery] = useState("");
  const [drafts, setDrafts] = useState<Record<string, string>>({});
  const [editing, setEditing] = useState<string | null>(null);
  const [saving, setSaving] = useState<string | null>(null);
  const [savedId, setSavedId] = useState<string | null>(null);
  const [manualItem, setManualItem] = useState<InventoryLookup | null>(null);
  const [finalizing, setFinalizing] = useState(false);
  const desktop = usesDesktopCustomerKeyboard();
  const [keyboardOpen] = useFullKeyboardVisible();

  const load = useCallback(async () => {
    setLoading(true);
    try {
      const result = await client.items(session.sessionId);
      setItems(result.items);
      onSession(result.session);
    } catch (error) {
      onMessage((error as Error).message);
    } finally {
      setLoading(false);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [client, session.sessionId]);

  useEffect(() => { void load(); }, [load]);

  const visible = useMemo(() => filterItems(items, tab, manual && tab === "current" ? "" : query), [items, tab, query, manual]);

  async function save(itemId: string) {
    const quantity = parseCountInput(drafts[itemId] ?? "");
    if (quantity === null) {
      onMessage("أدخل كمية صحيحة. الصفر مسموح والسالبة غير مسموحة.");
      return;
    }
    if (saving) {
      onMessage("جاري حفظ هذا الصنف. انتظر لحظة ثم أعد المحاولة.");
      return;
    }
    setSaving(itemId);
    onMessage("");
    try {
      const result = await client.count(session.sessionId, itemId, quantity);
      onSession(result.session);
      if (result.item) {
        const saved = result.item;
        setItems((current) => {
          const exists = current.some((item) => item.itemId === saved.itemId);
          return exists ? current.map((item) => (item.itemId === saved.itemId ? saved : item)) : [...current, saved];
        });
      }
      setDrafts((current) => { const next = { ...current }; delete next[itemId]; return next; });
      setEditing(null);
      setSavedId(itemId);
      if (manual) setManualItem(null);
    } catch (error) {
      onMessage((error as Error).message);
    } finally {
      setSaving(null);
    }
  }

  async function lookup(event: FormEvent) {
    event.preventDefault();
    const code = query.trim();
    if (!code) return;
    onMessage("");
    try {
      const found = await client.lookup(session.sessionId, code);
      if (manual) {
        setManualItem(found);
        setTab("current");
        setDrafts((current) => ({ ...current, [found.itemId]: found.countedQuantity === null ? "" : String(found.countedQuantity) }));
        if (found.isCounted) onMessage("تم جرد هذا الصنف سابقًا، ويمكنك تعديل الكمية.");
        setQuery("");
      } else {
        setTab(found.isCounted ? "counted" : "current");
        setQuery(found.itemNumber);
        if (found.isCounted) setEditing(found.itemId);
      }
      window.setTimeout(() => document.querySelector<HTMLInputElement>(`[data-count-item='${found.itemId}']`)?.focus(), 0);
    } catch (error) {
      onMessage((error as Error).message);
    }
  }

  function finalize() {
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
        <input
          data-count-item={itemId}
          inputMode="decimal"
          aria-label={`الكمية الفعلية ${label}`}
          value={value}
          onChange={(event) => setDrafts((current) => ({ ...current, [itemId]: event.target.value }))}
          onKeyDown={(event) => { if (event.key === "Enter") { event.preventDefault(); void save(itemId); } }}
        />
        <button type="button" aria-label="زيادة الكمية" onClick={() => setDrafts((current) => ({ ...current, [itemId]: stepQuantity(current[itemId] ?? "", 1) }))}>+</button>
        <button type="button" className="primary-button" disabled={saving === itemId} onClick={() => void save(itemId)}>{saving === itemId ? "جاري الحفظ..." : "حفظ"}</button>
      </div>
    );
  }

  const progress = manual
    ? `تم جرد ${session.countedItems} صنف`
    : `تم جرد ${session.countedItems} من ${session.totalItems} صنف`;

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
          {manual ? "الجرد الحالي" : `لم يتم الجرد (${Math.max(session.totalItems - session.countedItems, 0)})`}
        </button>
        <button type="button" role="tab" aria-selected={tab === "counted"} className={tab === "counted" ? "is-active" : undefined} onClick={() => setTab("counted")}>
          تم الجرد ({session.countedItems})
        </button>
      </div>

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
        {!loading && !(manual && tab === "current") ? visible.map((item) => (
          <article key={item.itemId} className={savedId === item.itemId ? "inventory-row is-saved" : "inventory-row"}>
            <div className="inventory-row-name">
              <span className="inventory-number">{item.itemNumber}</span>
              <strong>{item.itemName || "صنف بدون اسم"}</strong>
            </div>
            {item.isCounted && editing !== item.itemId ? (
              <div className="inventory-counted">
                {savedId === item.itemId ? <span className="inventory-saved">تم حفظ العد</span> : null}
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
      </div>

      <footer className="inventory-actions">
        <button type="button" className="ghost-button" disabled={finalizing} onClick={onSaveAndExit}>حفظ وخروج</button>
        <button type="button" className="primary-button" disabled={finalizing || session.countedItems === 0} onClick={finalize}>
          {finalizing ? "جاري التثبيت..." : "تثبيت الجرد"}
        </button>
      </footer>
      {desktop ? <KeyboardToggle /> : null}
      {desktop && keyboardOpen ? <FullKeyboard onEnter={() => (document.activeElement as HTMLElement | null)?.dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", bubbles: true }))} /> : null}
    </div>
  );
}
