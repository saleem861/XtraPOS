import { ArrowDown, ArrowUp, Plus, Save, Sparkles, Trash2 } from "lucide-react";
import { useCallback, useEffect, useMemo, useState } from "react";
import { createItemClient } from "../api/items";
import {
  createQuickItemsClient,
  moveItem,
  quickItemIcon,
  type QuickItem,
  type QuickItemsManage,
  type QuickItemSuggestion,
} from "../api/quickItems";
import { distinctBy } from "../distinct";
import type { DeviceSession } from "../types/setup";
import { NumericKeypad } from "./NumericKeypad";
import { ScreenHeader } from "./ScreenHeader";
import { Waiting } from "./Waiting";
import "./quickItems.css";

const ALL = "";

/**
 * ضبط «الأصناف السريعة»: تُفتح على «كل الفروع» (القائمة الافتراضية لأي فرع بلا قائمة خاصة)،
 * ويمكن اختيار أي فرع. الحفظ نسخة جديدة كاملة تُرحَّل إلى المركز وتصل إلى الفروع.
 */
export function QuickItemsSettingsScreen({ session, onBack }: { session: DeviceSession; onBack: () => void }) {
  const client = useMemo(() => createQuickItemsClient(session), [session]);
  const items = useMemo(() => createItemClient(session), [session]);
  const [branchId, setBranchId] = useState(ALL);
  const [data, setData] = useState<QuickItemsManage | null>(null);
  const [list, setList] = useState<QuickItem[]>([]);
  const [dirty, setDirty] = useState(false);
  const [number, setNumber] = useState("");
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [suggesting, setSuggesting] = useState(false);
  const [suggestions, setSuggestions] = useState<QuickItemSuggestion[] | null>(null);
  const [picked, setPicked] = useState<string[]>([]);
  const [error, setError] = useState("");
  const [message, setMessage] = useState("");

  const load = useCallback(async (scope: string) => {
    setLoading(true);
    setError("");
    setSuggestions(null);
    try {
      const result = await client.manage(scope || null);
      setData(result);
      setList(result.lines);
      setDirty(false);
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : "تعذر تحميل الأصناف السريعة.");
    } finally {
      setLoading(false);
    }
  }, [client]);

  useEffect(() => { void load(branchId); }, [branchId, load]);

  const branches = useMemo(() => distinctBy(data?.options.branches, (branch) => branch.name), [data]);
  const max = data?.maxItems ?? 30;

  async function addByNumber() {
    const wanted = number.trim();
    if (!wanted) return;
    setError("");
    setMessage("");
    if (list.some((item) => item.itemNumber === wanted)) {
      setError("الصنف موجود في القائمة.");
      return;
    }
    if (list.length >= max) {
      setError(`الحد الأقصى ${max} صنفاً.`);
      return;
    }
    try {
      const page = await items.loadItems({ search: wanted, activity: "active", page: 1, pageSize: 25, sort: "number", exact: true });
      const found = page.items.find((item) => item.itemNumber === wanted);
      if (!found) {
        setError(`لا يوجد صنف فعّال بالرقم ${wanted}.`);
        return;
      }
      setList((current) => [...current, { itemNumber: found.itemNumber, itemName: found.itemName, classification: found.classification ?? null, active: true }]);
      setNumber("");
      setDirty(true);
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : "تعذر البحث عن الصنف.");
    }
  }

  async function suggest() {
    setSuggesting(true);
    setError("");
    try {
      const result = await client.suggest(branchId || null, 30, 10);
      setSuggestions(result.items);
      setPicked(result.items.map((item) => item.itemNumber));
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : "تعذر حساب الاقتراح.");
    } finally {
      setSuggesting(false);
    }
  }

  function acceptSuggestions(replace: boolean) {
    if (!suggestions) return;
    const chosen = suggestions.filter((item) => picked.includes(item.itemNumber));
    setList((current) => {
      const base = replace ? [] : current;
      const merged = [...base, ...chosen.filter((item) => !base.some((row) => row.itemNumber === item.itemNumber))];
      return merged.slice(0, max);
    });
    setSuggestions(null);
    setDirty(true);
  }

  async function save() {
    setSaving(true);
    setError("");
    setMessage("");
    try {
      const result = await client.save(branchId || null, list.map((item) => item.itemNumber));
      setMessage(result.message);
      await load(branchId);
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : "تعذر حفظ الأصناف السريعة.");
    } finally {
      setSaving(false);
    }
  }

  const scopeName = branchId ? branches.find((branch) => branch.id === branchId)?.name ?? "الفرع" : "كل الفروع";
  return (
    <section className="new-customer sales-review quick-settings" dir="rtl" aria-label="الأصناف السريعة">
      <ScreenHeader title="الأصناف السريعة" onBack={onBack} />
      <div className="quick-settings-body" style={{ position: "relative" }}>
        <div className="quick-settings-bar">
          <label>القائمة
            <select value={branchId} disabled={saving} onChange={(event) => { setMessage(""); setBranchId(event.target.value); }}>
              <option value={ALL}>كل الفروع (الافتراضية)</option>
              {branches.map((branch) => <option key={branch.id} value={branch.id}>{branch.name}</option>)}
            </select>
          </label>
          <label>رقم الصنف
            <input
              value={number}
              inputMode="numeric"
              placeholder="أدخل الرقم"
              onChange={(event) => setNumber(event.target.value)}
              onKeyDown={(event) => { if (event.key === "Enter") { event.preventDefault(); void addByNumber(); } }}
            />
          </label>
          <button type="button" onClick={() => void addByNumber()} disabled={!number.trim()}><Plus aria-hidden="true" />إضافة</button>
          <button type="button" onClick={() => void suggest()} disabled={suggesting}><Sparkles aria-hidden="true" />اقتراح حسب الأكثر مبيعاً</button>
          <button type="button" className="is-primary" onClick={() => void save()} disabled={saving || !dirty}><Save aria-hidden="true" />حفظ</button>
        </div>

        {data?.notice ? <p className="cash-errors-notice">{data.notice}</p> : null}
        <p className="quick-settings-hint">
          {branchId
            ? data?.inherited
              ? `لا توجد قائمة خاصة بفرع ${scopeName}؛ يظهر فيه الآن قائمة «كل الفروع». الحفظ هنا ينشئ قائمته الخاصة، وحفظ قائمة فارغة يعيده إلى «كل الفروع».`
              : `قائمة خاصة بفرع ${scopeName}. حفظ قائمة فارغة يعيده إلى قائمة «كل الفروع».`
            : "قائمة «كل الفروع» تظهر في كل فرع ليست له قائمة خاصة."}
          {data?.set && !data.inherited ? ` آخر حفظ: ${data.set.createdByName ?? "—"} ${new Date(data.set.createdAt).toLocaleString("ar-PS-u-nu-latn")}.` : ""}
          {` (${list.length} من ${max})`}
        </p>

        {suggestions ? (
          <div className="quick-settings-suggest" role="region" aria-label="اقتراح الأصناف">
            <strong>الأكثر ظهوراً في فواتير {scopeName} خلال آخر 30 يوماً</strong>
            {suggestions.length === 0 ? <p>لا توجد مبيعات كافية للاقتراح.</p> : (
              <ul>
                {suggestions.map((item, index) => (
                  <li key={item.itemNumber}>
                    <label>
                      <input
                        type="checkbox"
                        checked={picked.includes(item.itemNumber)}
                        onChange={() => setPicked((current) => current.includes(item.itemNumber) ? current.filter((value) => value !== item.itemNumber) : [...current, item.itemNumber])}
                      />
                      <span>{index + 1}. {quickItemIcon(item)} {item.itemNumber} — {item.itemName} · في {item.invoices} فاتورة</span>
                    </label>
                  </li>
                ))}
              </ul>
            )}
            <div className="quick-settings-suggest-actions">
              <button type="button" disabled={picked.length === 0} onClick={() => acceptSuggestions(true)}>استبدال القائمة بالمختار</button>
              <button type="button" disabled={picked.length === 0} onClick={() => acceptSuggestions(false)}>إضافة المختار للقائمة</button>
              <button type="button" onClick={() => setSuggestions(null)}>إلغاء</button>
            </div>
          </div>
        ) : null}

        {error ? <p className="form-error" role="alert">{error}</p> : null}
        {message ? <p className="update-result is-ok" role="status">{message}</p> : null}
        {loading && !data ? <Waiting label="جاري تحميل الأصناف السريعة…" /> : null}

        {data ? (
          list.length === 0 ? <p className="quick-settings-hint">القائمة فارغة. أضف أصنافاً برقمها أو اطلب اقتراحاً.</p> : (
            <ol className="quick-settings-list">
              {list.map((item, index) => (
                <li key={item.itemNumber} className={item.active === false ? "quick-settings-row is-inactive" : "quick-settings-row"}>
                  <b>{index + 1}</b>
                  <span aria-hidden="true" style={{ fontSize: 22 }}>{quickItemIcon(item)}</span>
                  <strong>{item.itemNumber}</strong>
                  <span>{item.itemName}{item.active === false ? " (غير فعّال، لا يظهر في البيع)" : ""}</span>
                  <div>
                    <button type="button" aria-label={`رفع ${item.itemName}`} disabled={index === 0} onClick={() => { setList((current) => moveItem(current, index, -1)); setDirty(true); }}><ArrowUp aria-hidden="true" /></button>
                    <button type="button" aria-label={`خفض ${item.itemName}`} disabled={index === list.length - 1} onClick={() => { setList((current) => moveItem(current, index, 1)); setDirty(true); }}><ArrowDown aria-hidden="true" /></button>
                    <button type="button" aria-label={`حذف ${item.itemName}`} onClick={() => { setList((current) => current.filter((row) => row.itemNumber !== item.itemNumber)); setDirty(true); }}><Trash2 aria-hidden="true" /></button>
                  </div>
                </li>
              ))}
            </ol>
          )
        ) : null}

        <div className="quick-settings-pad">
          <NumericKeypad onDigit={(digit) => setNumber((current) => current + digit)} onClear={() => setNumber("")} />
        </div>
        {(loading && data) || saving ? <Waiting variant="overlay" label={saving ? "جاري الحفظ…" : "جاري التحميل…"} /> : null}
      </div>
    </section>
  );
}
