import { useEffect, useRef, useState, type KeyboardEvent } from "react";
import { Save } from "lucide-react";
import { NumericKeypad } from "./NumericKeypad";
import type { SpecialPriceRow } from "../api/customers";
import type { ItemListItem } from "../catalog/types";

export function targetUnitPrice(bundlePrice: string, quantity: string): string {
  const bundle = Number(bundlePrice);
  const qty = Number(quantity);
  if (!Number.isFinite(bundle) || !Number.isFinite(qty) || bundle <= 0 || qty <= 0) return "";
  return (Math.round((bundle / qty) * 10000) / 10000).toFixed(2);
}

export function ExceptionalPricesScreen({
  customerNumber,
  customerName,
  rows,
  currentPrice,
  onSearch,
  onCommit,
  onDelete,
  onClose,
  bindSearchEnter,
}: {
  customerNumber: string;
  customerName: string;
  rows: SpecialPriceRow[];
  currentPrice: (item: ItemListItem) => Promise<string>;
  onSearch: (term: string) => Promise<ItemListItem[]>;
  onCommit: (row: { item: ItemListItem; minimumQuantity: string; bundlePrice: string; targetUnitPrice: string; existing?: SpecialPriceRow }) => Promise<void>;
  onDelete: (row: SpecialPriceRow) => Promise<void>;
  onClose: () => void;
  bindSearchEnter?: (handler: () => void) => void;
}) {
  const searchRef = useRef<HTMLInputElement>(null);
  const quantityRef = useRef<HTMLInputElement>(null);
  const bundleRef = useRef<HTMLInputElement>(null);
  const matchesRef = useRef<ItemListItem[]>([]);
  const [term, setTerm] = useState("");
  const [matches, setMatches] = useState<ItemListItem[]>([]);
  const [item, setItem] = useState<ItemListItem | null>(null);
  const [customerUnit, setCustomerUnit] = useState("");
  const [quantity, setQuantity] = useState("1");
  const [bundle, setBundle] = useState("");
  const [editing, setEditing] = useState<SpecialPriceRow | null>(null);
  const [modal, setModal] = useState<"quantity" | "price" | null>(null);
  const [activeField, setActiveField] = useState<"quantity" | "price">("quantity");
  const [fresh, setFresh] = useState(true);
  const active = rows.filter((row) => row.isActive);
  const target = targetUnitPrice(bundle, quantity);

  useEffect(() => {
    matchesRef.current = matches;
  }, [matches]);

  useEffect(() => {
    bindSearchEnter?.(() => {
      const first = matchesRef.current[0];
      if (first) void choose(first);
    });
  });

  useEffect(() => {
    if (!modal) searchRef.current?.focus();
  }, [modal, rows.length]);

  useEffect(() => {
    if (!modal) return;
    const field = modal === "quantity" ? quantityRef.current : bundleRef.current;
    field?.focus();
    setActiveField(modal);
    setFresh(true);
  }, [modal]);

  async function search(value: string) {
    setTerm(value);
    setMatches(value.trim() ? await onSearch(value.trim()) : []);
  }

  async function choose(next: ItemListItem) {
    setItem(next);
    setMatches([]);
    setTerm("");
    setEditing(null);
    setQuantity("1");
    setBundle("");
    setCustomerUnit(await currentPrice(next));
    searchRef.current?.focus();
  }

  function openModal(which: "quantity" | "price", row?: SpecialPriceRow) {
    if (row) {
      setEditing(row);
      setQuantity(String(row.minimumQuantity));
      setBundle(Number(row.bundlePrice).toFixed(2));
      setItem({ itemId: row.itemId, itemNumber: row.itemNumber, itemName: row.itemName, isActive: true, isProhibited: false });
    }
    if (!item && !row) return;
    setModal(which);
  }

  async function confirm() {
    if (!item) return;
    const nextTarget = targetUnitPrice(bundle, quantity);
    if (!nextTarget) return;
    await onCommit({ item, minimumQuantity: quantity, bundlePrice: bundle, targetUnitPrice: nextTarget, existing: editing ?? undefined });
    setModal(null);
    setItem(null);
    setEditing(null);
    setQuantity("1");
    setBundle("");
  }

  function push(digit: string) {
    const apply = (current: string) => {
      const base = fresh ? "" : current;
      if (digit === ".") return base.includes(".") ? base : `${base || "0"}.`;
      if (digit === "⌫") return base.slice(0, -1);
      const next = `${base}${digit}`.replace(/^0+(?=\d)/, "");
      const fraction = next.split(".")[1];
      return (fraction?.length ?? 0) > 2 ? base : next;
    };
    if (activeField === "quantity") setQuantity(apply);
    else setBundle(apply);
    setFresh(false);
  }

  function onModalKeyDown(event: KeyboardEvent<HTMLFormElement>) {
    if (event.key === "Escape") {
      event.preventDefault();
      setModal(null);
      return;
    }
    const numpad = /^Numpad([0-9])$/.exec(event.code);
    if (/^[0-9]$/.test(event.key) || numpad) {
      event.preventDefault();
      push(numpad ? numpad[1] : event.key);
      return;
    }
    if (event.key === "." || event.key === "," || event.code === "NumpadDecimal") {
      event.preventDefault();
      push(".");
      return;
    }
    if (event.key === "Backspace" || event.key === "Delete") {
      event.preventDefault();
      if (event.key === "Delete") {
        if (activeField === "quantity") setQuantity("");
        else setBundle("");
        setFresh(false);
        return;
      }
      push("⌫");
    }
  }

  const standard = item?.resolvedPrice != null ? Number(item.resolvedPrice).toFixed(2) : "—";

  return (
    <>
      <div className="exceptional-page">
        <div className="exceptional-customer">
          <span>رقم الزبون <b>{customerNumber || "—"}</b></span>
          <span>اسم الزبون <b>{customerName || "—"}</b></span>
        </div>
        <label className="exceptional-search">البحث عن صنف
          <input
            id="exceptional-search"
            ref={searchRef}
            aria-label="البحث عن صنف"
            placeholder="رقم الصنف / اسم الصنف / باركود ..."
            value={term}
            onChange={(event) => void search(event.target.value)}
            onKeyDown={(event) => {
              if (event.key === "Enter" && matches[0]) {
                event.preventDefault();
                void choose(matches[0]);
              }
            }}
          />
        </label>
        {matches.length > 0 ? (
          <ul className="exceptional-matches">
            {matches.map((match) => (
              <li key={match.itemId}><button type="button" onClick={() => void choose(match)}>{match.itemNumber} {match.itemName}</button></li>
            ))}
          </ul>
        ) : null}
        <div className="exceptional-entry">
          <span>رقم الصنف <b>{item?.itemNumber || "—"}</b></span>
          <span>اسم الصنف <b>{item?.itemName || "—"}</b></span>
          <span>السعر العادي <b>{item ? standard : "—"}</b></span>
          <span>سعر الزبون الحالي <b>{item ? (customerUnit || "—") : "—"}</b></span>
          <button type="button" disabled={!item} onClick={() => openModal("quantity")}>الكمية <b>{quantity}</b></button>
          <button type="button" disabled={!item} onClick={() => openModal("price")}>السعر الاستثنائي الجديد <b>{item && target ? target : "—"}</b></button>
          <button type="button" className="is-add" disabled={!item} onClick={() => openModal("price")}>إضافة الصنف</button>
        </div>
        <h3>الأصناف الاستثنائية الحالية</h3>
        <div className="exceptional-table-wrap" style={{ overflowY: "auto" }}>
          <table>
            <thead>
              <tr>
                <th>#</th>
                <th>رقم الصنف</th>
                <th>اسم الصنف</th>
                <th>السعر الأصلي</th>
                <th>الكمية</th>
                <th>السعر الاستثنائي</th>
                <th>تعديل</th>
                <th>حذف</th>
              </tr>
            </thead>
            <tbody>
              {active.map((row, index) => (
                <tr key={row.customerItemSpecialPriceId}>
                  <td>{index + 1}</td>
                  <td>{row.itemNumber}</td>
                  <td>{row.itemName}</td>
                  <td>{Number(row.basePrice).toFixed(2)}</td>
                  <td>{row.minimumQuantity}</td>
                  <td>{Number(row.specialPrice).toFixed(2)}</td>
                  <td><button type="button" onClick={() => openModal("quantity", row)}>تعديل</button></td>
                  <td><button type="button" className="is-delete" onClick={() => void onDelete(row)}>حذف</button></td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        <div className="exceptional-actions">
          <button type="button" className="is-save" onClick={onClose}><Save aria-hidden="true" />حفظ التعديلات</button>
        </div>
      </div>
      {modal ? (
        <div className="pos-overlay">
          <form className="exceptional-modal" role="dialog" aria-label="الكمية والسعر" onSubmit={(event) => { event.preventDefault(); void confirm(); }} onKeyDown={onModalKeyDown}>
            <header>{item?.itemName || "الصنف"}</header>
            <p>كل {quantity || "0"} بـ {Number(bundle || 0).toFixed(2)}</p>
            <div className="exceptional-modal-fields">
              <label>الكمية<input ref={quantityRef} aria-label="كمية الاستثناء" inputMode="decimal" value={quantity} onFocus={() => { setActiveField("quantity"); setFresh(true); }} onChange={(event) => setQuantity(event.target.value)} /></label>
              <label>سعر المجموعة<input ref={bundleRef} aria-label="سعر المجموعة" inputMode="decimal" value={bundle} onFocus={() => { setActiveField("price"); setFresh(true); }} onChange={(event) => setBundle(event.target.value)} /></label>
            </div>
            <strong>السعر المستهدف للوحدة: {target || "—"} شيكل</strong>
            <NumericKeypad
              allowDecimal
              onDigit={push}
              onClear={() => {
                if (activeField === "quantity") setQuantity("");
                else setBundle("");
                setFresh(false);
              }}
            />
            <div className="exceptional-modal-actions">
              <button className="nav-button" type="button" onClick={() => setModal(null)}>إلغاء</button>
              <button className="primary-button" type="submit">تثبيت</button>
            </div>
          </form>
        </div>
      ) : null}
    </>
  );
}
