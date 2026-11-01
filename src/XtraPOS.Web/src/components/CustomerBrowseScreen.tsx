import { useEffect, useMemo, useState } from "react";
import { Trash2 } from "lucide-react";
import { SetupClientError } from "../api/client";
import { createCustomerClient, type CustomerListItem } from "../api/customers";
import { usesDesktopCustomerKeyboard } from "../customers/newCustomerForm";
import type { DeviceSession } from "../types/setup";
import { FullKeyboard } from "./FullKeyboard";
import { ScreenHeader } from "./ScreenHeader";
import { NewCustomerScreen } from "./NewCustomerScreen";

interface CustomerBrowseScreenProps {
  session: DeviceSession;
  permissions: string[];
  onBack: () => void;
}

export function CustomerBrowseScreen({ session, permissions, onBack }: CustomerBrowseScreenProps) {
  const customers = useMemo(() => createCustomerClient(session), [session]);
  const canEdit = permissions.includes("Customers.Manage") || permissions.includes("System.Administer");
  const canDelete = canEdit;
  const [term, setTerm] = useState("");
  const [query, setQuery] = useState("");
  const [page, setPage] = useState(1);
  const [rows, setRows] = useState<CustomerListItem[]>([]);
  const [total, setTotal] = useState(0);
  const [state, setState] = useState<"idle" | "loading" | "ready" | "error">("idle");
  const [message, setMessage] = useState("");
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [pendingDelete, setPendingDelete] = useState<CustomerListItem | null>(null);
  const [reloadKey, setReloadKey] = useState(0);
  const [desktopKeyboard, setDesktopKeyboard] = useState(usesDesktopCustomerKeyboard);

  useEffect(() => {
    const media = window.matchMedia("(pointer: fine) and (hover: hover)");
    const sync = () => setDesktopKeyboard(usesDesktopCustomerKeyboard());
    media.addEventListener("change", sync);
    return () => media.removeEventListener("change", sync);
  }, []);

  useEffect(() => {
    const handle = window.setTimeout(() => {
      setQuery(term.trim());
      setPage(1);
    }, 300);
    return () => window.clearTimeout(handle);
  }, [term]);

  useEffect(() => {
    if (!query) {
      setRows([]);
      setTotal(0);
      setState("idle");
      return;
    }
    let active = true;
    setState("loading");
    customers.search(query, page).then((result) => {
      if (!active) return;
      setRows(result.customers);
      setTotal(result.totalCount);
      setState("ready");
      setMessage("");
    }).catch(() => {
      if (!active) return;
      setState("error");
      setMessage("تعذر البحث عن الزبائن.");
    });
    return () => {
      active = false;
    };
  }, [customers, query, page, reloadKey]);

  async function confirmDelete() {
    if (!pendingDelete) return;
    try {
      await customers.remove(pendingDelete.customerId);
      setPendingDelete(null);
      setMessage("تم حذف الزبون.");
      setReloadKey((value) => value + 1);
    } catch (error) {
      setPendingDelete(null);
      setState("error");
      setMessage(error instanceof SetupClientError ? error.message : "تعذر حذف الزبون.");
    }
  }

  if (selectedId) {
    return (
      <NewCustomerScreen
        session={session}
        customerId={selectedId}
        permissions={permissions}
        readOnly={!canEdit}
        title={canEdit ? "تعديل زبون" : "عرض زبون"}
        onBack={() => setSelectedId(null)}
        onSaved={() => {
          setReloadKey((value) => value + 1);
        }}
      />
    );
  }

  return (
    <section className={desktopKeyboard ? "new-customer full-keyboard-screen has-keyboard" : "new-customer"} dir="rtl" aria-label="استعراض الزبائن">
      <ScreenHeader title="استعراض الزبائن" onBack={onBack} />
      <header className="new-customer-bar shell-page-bar">
        <strong className="new-customer-brand">XtraPOS</strong>
        <h2>استعراض الزبائن</h2>
        <button type="button" className="new-customer-back" onClick={onBack}>رجوع</button>
      </header>
      <div className="customer-search-content">
        <label>
          <span>بحث</span>
          <input aria-label="بحث عن زبون" value={term} placeholder="رقم الزبون أو الاسم أو الهاتف" onChange={(event) => setTerm(event.target.value)} />
        </label>
        {state === "loading" ? <p>جارٍ البحث...</p> : null}
        {state === "error" ? <p className="status-error">{message}</p> : null}
        {state === "ready" && rows.length === 0 ? <p>لا توجد نتائج.</p> : null}
        {message && state !== "error" ? <p className="new-customer-success">{message}</p> : null}
        <div className="customer-browse-results" role="table" aria-label="نتائج الزبائن">
          <div className="customer-browse-row customer-browse-head" role="row">
            <span role="columnheader">رقم الزبون</span>
            <span role="columnheader">اسم الزبون</span>
            <span role="columnheader">التصنيف</span>
            <span role="columnheader">إجراءات</span>
          </div>
          {rows.map((row, index) => (
            <div className={index % 2 === 1 ? "customer-browse-row is-alt" : "customer-browse-row"} role="row" key={row.customerId}>
              <span className="customer-browse-number" role="cell" dir="ltr">{row.customerNumber ?? "—"}</span>
              <span className="customer-browse-name" role="cell">{row.name}</span>
              <span className="customer-browse-class" role="cell">{row.classificationName ?? "—"}</span>
              <span className="customer-browse-actions" role="cell">
                <button type="button" onClick={() => setSelectedId(row.customerId)}>عرض</button>
                {canDelete ? <button type="button" className="customer-browse-delete" onClick={() => setPendingDelete(row)}><Trash2 size={16} aria-hidden="true" />حذف</button> : null}
              </span>
            </div>
          ))}
        </div>
        {total > rows.length ? (
          <div className="action-row">
            <button type="button" disabled={page <= 1} onClick={() => setPage((current) => current - 1)}>السابق</button>
            <button type="button" disabled={page * 25 >= total} onClick={() => setPage((current) => current + 1)}>التالي</button>
          </div>
        ) : null}
      </div>
      {desktopKeyboard ? <FullKeyboard onEnter={() => undefined} /> : null}
      {pendingDelete ? (
        <div className="customer-delete-dialog" role="presentation">
          <div className="customer-delete-card" role="dialog" aria-modal="true" aria-label="تأكيد حذف الزبون">
            <strong>{pendingDelete.name}</strong>
            <span dir="ltr">{pendingDelete.customerNumber ?? "—"}</span>
            <p>هل أنت متأكد من حذف هذا الزبون؟ لا يمكن التراجع عن عملية الحذف.</p>
            <div className="customer-delete-actions">
              <button type="button" onClick={() => setPendingDelete(null)}>إلغاء</button>
              <button type="button" className="customer-browse-delete" onClick={() => void confirmDelete()}>تأكيد الحذف</button>
            </div>
          </div>
        </div>
      ) : null}
    </section>
  );
}
