import { useEffect, useState } from "react";
import { blank, formatFactor, formatPrice, formatQuantity, inventoryStatus } from "../catalog/format";
import type { ItemCatalogClient, ItemDetail, ItemEditForm, ItemInventory, ItemMovement, ItemPrice, ItemUnits } from "../catalog/types";
import { ScreenHeader } from "./ScreenHeader";

type DetailTab = "profile" | "prices" | "units" | "inventory" | "movements";

const tabs: { id: DetailTab; label: string }[] = [
  { id: "profile", label: "بيانات الصنف" },
  { id: "prices", label: "الأسعار" },
  { id: "units", label: "الوحدات" },
  { id: "inventory", label: "المخزون" },
  { id: "movements", label: "الحركة" },
];

interface ItemDetailScreenProps {
  client: ItemCatalogClient;
  itemId: string;
  onHome: () => void;
  onBack: () => void;
}

export function ItemDetailScreen({ client, itemId, onBack }: ItemDetailScreenProps) {
  const [tab, setTab] = useState<DetailTab>("profile");
  const [detail, setDetail] = useState<ItemDetail | null>(null);
  const [prices, setPrices] = useState<ItemPrice[] | null>(null);
  const [units, setUnits] = useState<ItemUnits | null>(null);
  const [inventory, setInventory] = useState<ItemInventory | null>(null);
  const [movements, setMovements] = useState<ItemMovement[] | null>(null);
  const [form, setForm] = useState<ItemEditForm | null>(null);
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");

  useEffect(() => {
    let cancelled = false;
    void Promise.all([
      client.loadDetail(itemId),
      client.loadPrices(itemId),
      client.loadUnits(itemId),
      client.loadInventory(itemId),
      client.loadMovements(itemId),
      client.loadEditForm(itemId),
    ])
      .then(([nextDetail, nextPrices, nextUnits, nextInventory, nextMovements, nextForm]) => {
        if (cancelled) {
          return;
        }
        setDetail(nextDetail);
        setPrices(nextPrices);
        setUnits(nextUnits);
        setInventory(nextInventory);
        setMovements(nextMovements);
        setForm(nextForm);
      })
      .catch(() => {
        if (!cancelled) {
          setError("تعذر تحميل تفاصيل الصنف.");
        }
      });
    return () => {
      cancelled = true;
    };
  }, [client, itemId]);

  return (
    <section className="catalog">
      <ScreenHeader title="تفاصيل الصنف" onBack={onBack} />
      <header className="catalog-header">
        {detail ? <p>{detail.itemNumber} — {detail.itemName}</p> : null}
      </header>
      <div className="tab-row" role="tablist" aria-label="أقسام الصنف">
        {tabs.map((item) => (
          <button
            key={item.id}
            className={tab === item.id ? "tab is-selected" : "tab"}
            type="button"
            role="tab"
            aria-selected={tab === item.id}
            onClick={() => setTab(item.id)}
          >
            {item.label}
          </button>
        ))}
      </div>
      {error ? <p className="form-error">{error}</p> : null}
      {notice ? <p className="catalog-status">{notice}</p> : null}
      {!detail && !error ? <p className="catalog-status">جاري التحميل</p> : null}
      {detail && tab === "profile" ? <ProfileTab detail={detail} form={form} client={client} itemId={itemId} onSaved={setNotice} onError={setError} /> : null}
      {prices && tab === "prices" ? <PricesTab prices={prices} form={form} client={client} itemId={itemId} onSaved={setNotice} onError={setError} /> : null}
      {units && tab === "units" ? <UnitsTab units={units} form={form} client={client} itemId={itemId} onSaved={setNotice} onError={setError} /> : null}
      {inventory && tab === "inventory" ? <InventoryTab inventory={inventory} /> : null}
      {movements && tab === "movements" ? <MovementsTab movements={movements} /> : null}
    </section>
  );
}

function ProfileTab({
  detail,
  form,
  client,
  itemId,
  onSaved,
  onError,
}: {
  detail: ItemDetail;
  form: ItemEditForm | null;
  client: ItemCatalogClient;
  itemId: string;
  onSaved: (message: string) => void;
  onError: (message: string) => void;
}) {
  const linked = form?.topology === "CentralLinked" || form?.topology === "Central";
  const masterEditable = Boolean(form?.canEditMaster && !form.masterReadOnly);
  const branchEditable = Boolean(form?.canEditBranchState);
  const [name, setName] = useState(form?.itemName ?? detail.itemName);
  const [itemType, setItemType] = useState(form?.itemType ?? detail.itemType ?? "");
  const [manufacturer, setManufacturer] = useState(form?.manufacturer ?? detail.manufacturer ?? "");
  const [active, setActive] = useState(form?.isActive ?? detail.isActive);
  const [prohibited, setProhibited] = useState(form?.isProhibited ?? detail.isProhibited);
  const [published, setPublished] = useState(form?.isPublishedOnline ?? detail.isPublishedOnline);
  const [unitCost, setUnitCost] = useState(form?.unitCost == null ? "" : String(form.unitCost));
  const [available, setAvailable] = useState(form?.isAvailable ?? true);
  const [branchActive, setBranchActive] = useState(form?.branchActive ?? true);

  useEffect(() => {
    if (!form) {
      return;
    }
    setName(form.itemName);
    setItemType(form.itemType ?? "");
    setManufacturer(form.manufacturer ?? "");
    setActive(form.isActive);
    setProhibited(form.isProhibited);
    setPublished(form.isPublishedOnline);
    setUnitCost(form.unitCost == null ? "" : String(form.unitCost));
    setAvailable(form.isAvailable);
    setBranchActive(form.branchActive);
  }, [form]);

  const parent = detail.parentItemNumber
    ? `${detail.parentItemNumber} — ${detail.parentItemName ?? ""}`.trim()
    : "—";

  async function saveMaster() {
    if (!form) {
      return;
    }
    onError("");
    try {
      await client.saveMaster(itemId, {
        itemNumber: detail.itemNumber,
        itemName: name,
        itemType,
        manufacturer,
        color: form.color,
        taste: form.taste,
        size: form.size,
        packing: form.packing,
        classification: form.classification,
        countryOfOrigin: form.countryOfOrigin,
        productType: form.productType,
        parentItemId: form.parentItemId,
        isActive: active,
        isProhibited: prohibited,
        isPublishedOnline: published,
        rowVersion: form.rowVersion,
      });
      onSaved("تم حفظ البيانات الأساسية.");
    } catch (reason) {
      onError(reason instanceof Error ? reason.message : "تعذر حفظ البيانات الأساسية.");
    }
  }

  async function saveBranch() {
    onError("");
    try {
      await client.saveBranchState(itemId, {
        unitCost: unitCost.trim() === "" ? null : Number(unitCost),
        isAvailable: available,
        isActive: branchActive,
      });
      onSaved("تم حفظ بيانات هذا الفرع.");
    } catch (reason) {
      onError(reason instanceof Error ? reason.message : "تعذر حفظ بيانات الفرع.");
    }
  }

  return (
    <div className="stack">
      {form?.masterReadOnlyMessage ? <p className="form-error ownership-message">{form.masterReadOnlyMessage}</p> : null}
      <section>
        <h2>
          البيانات الأساسية للصنف
          {linked ? <span className="ownership-label">بيانات مركزية</span> : null}
        </h2>
        {masterEditable || form?.masterReadOnly ? (
          <div className="edit-grid">
            <Field label="رقم الصنف" value={detail.itemNumber} />
            <label>
              اسم الصنف
              <input aria-label="اسم الصنف" value={name} disabled={!masterEditable} onChange={(event) => setName(event.target.value)} />
            </label>
            <label>
              نوع الصنف
              <input aria-label="نوع الصنف" value={itemType} disabled={!masterEditable} onChange={(event) => setItemType(event.target.value)} />
            </label>
            <label>
              الشركة
              <input aria-label="الشركة" value={manufacturer} disabled={!masterEditable} onChange={(event) => setManufacturer(event.target.value)} />
            </label>
            <label className="check-line">
              <input type="checkbox" aria-label="الصنف نشط" checked={active} disabled={!masterEditable} onChange={(event) => setActive(event.target.checked)} />
              نشط
            </label>
            <label className="check-line">
              <input type="checkbox" aria-label="ممنوع البيع" checked={prohibited} disabled={!masterEditable} onChange={(event) => setProhibited(event.target.checked)} />
              ممنوع البيع
            </label>
            <label className="check-line">
              <input type="checkbox" aria-label="منشور إلكترونيًا" checked={published} disabled={!masterEditable} onChange={(event) => setPublished(event.target.checked)} />
              منشور إلكترونيًا
            </label>
            {masterEditable ? <button type="button" onClick={() => void saveMaster()}>حفظ البيانات الأساسية</button> : null}
          </div>
        ) : (
          <dl className="details">
            <Field label="رقم الصنف" value={detail.itemNumber} />
            <Field label="اسم الصنف" value={detail.itemName} />
            <Field label="نوع الصنف" value={blank(detail.itemType)} />
            <Field label="الشركة" value={blank(detail.manufacturer)} />
            <Field label="اللون" value={blank(detail.color)} />
            <Field label="الطعم" value={blank(detail.taste)} />
            <Field label="الحجم" value={blank(detail.size)} />
            <Field label="التغليف" value={blank(detail.packing)} />
            <Field label="التصنيف" value={blank(detail.classification)} />
            <Field label="بلد المنشأ" value={blank(detail.countryOfOrigin)} />
            <Field label="نوع المنتج" value={blank(detail.productType)} />
            <Field label="الحالة" value={detail.isActive ? "نشط" : "غير نشط"} />
            <Field label="ممنوع البيع" value={detail.isProhibited ? "نعم" : "لا"} />
            <Field label="منشور إلكترونيًا" value={detail.isPublishedOnline ? "نعم" : "لا"} />
            <Field label="الصنف الأب" value={parent} />
          </dl>
        )}
      </section>
      <section>
        <h2>
          بيانات هذا الفرع
          <span className="ownership-label">خاص بهذا الفرع</span>
        </h2>
        {branchEditable ? (
          <div className="edit-grid">
            <label>
              تكلفة الوحدة
              <input aria-label="تكلفة الوحدة" value={unitCost} onChange={(event) => setUnitCost(event.target.value)} />
            </label>
            <label className="check-line">
              <input type="checkbox" aria-label="متاح في الفرع" checked={available} onChange={(event) => setAvailable(event.target.checked)} />
              متاح في الفرع
            </label>
            <label className="check-line">
              <input type="checkbox" aria-label="نشط في الفرع" checked={branchActive} onChange={(event) => setBranchActive(event.target.checked)} />
              نشط في الفرع
            </label>
            <button type="button" onClick={() => void saveBranch()}>حفظ بيانات الفرع</button>
          </div>
        ) : (
          <dl className="details">
            <Field label="تكلفة الوحدة" value={form?.unitCost == null ? "—" : formatPrice(form.unitCost)} />
            <Field label="متاح في الفرع" value={form?.isAvailable === false ? "لا" : "نعم"} />
            <Field label="نشط في الفرع" value={form?.branchActive === false ? "لا" : "نعم"} />
          </dl>
        )}
      </section>
    </div>
  );
}

function PricesTab({
  prices,
  form,
  client,
  itemId,
  onSaved,
  onError,
}: {
  prices: ItemPrice[];
  form: ItemEditForm | null;
  client: ItemCatalogClient;
  itemId: string;
  onSaved: (message: string) => void;
  onError: (message: string) => void;
}) {
  return (
    <table className="data-table">
      <thead>
        <tr>
          <th>السعر</th>
          <th>السعر الأساسي</th>
          <th>سعر هذا الفرع</th>
        </tr>
      </thead>
      <tbody>
        {prices.map((price) => (
          <PriceRow key={price.priceCode} price={price} form={form} client={client} itemId={itemId} onSaved={onSaved} onError={onError} />
        ))}
      </tbody>
    </table>
  );
}

function PriceRow({
  price,
  form,
  client,
  itemId,
  onSaved,
  onError,
}: {
  price: ItemPrice;
  form: ItemEditForm | null;
  client: ItemCatalogClient;
  itemId: string;
  onSaved: (message: string) => void;
  onError: (message: string) => void;
}) {
  const authority = form?.prices.find((item) => item.priceCode === price.priceCode);
  const defaultPrice = authority?.defaultPrice ?? price.defaultPrice ?? price.price;
  const branchPrice = price.branchPrice ?? authority?.branchPrice;
  const [defaultDraft, setDefaultDraft] = useState(defaultPrice == null ? "" : String(defaultPrice));
  const [branchDraft, setBranchDraft] = useState(branchPrice == null ? "" : String(branchPrice));
  const masterEditable = Boolean(form?.canEditMaster && !form.masterReadOnly);
  const branchEditable = Boolean(form?.canEditBranchPrice);

  async function saveDefault() {
    onError("");
    try {
      await client.saveDefaultPrice(itemId, {
        priceCode: price.priceCode,
        price: Number(defaultDraft),
        rowVersion: authority?.defaultRowVersion,
      });
      onSaved("تم حفظ السعر الأساسي.");
    } catch (reason) {
      onError(reason instanceof Error ? reason.message : "تعذر حفظ السعر الأساسي.");
    }
  }

  async function saveBranch() {
    onError("");
    try {
      await client.saveBranchPrice(itemId, {
        priceCode: price.priceCode,
        price: branchDraft.trim() === "" ? null : Number(branchDraft),
      });
      onSaved("تم حفظ سعر هذا الفرع.");
    } catch (reason) {
      onError(reason instanceof Error ? reason.message : "تعذر حفظ سعر هذا الفرع.");
    }
  }

  return (
    <tr>
      <td>{price.priceName}</td>
      <td>
        <div>{formatPrice(defaultPrice)}</div>
        {masterEditable ? (
          <div className="inline-edit">
            <input aria-label={`السعر الأساسي ${price.priceName}`} value={defaultDraft} onChange={(event) => setDefaultDraft(event.target.value)} />
            <button type="button" onClick={() => void saveDefault()}>حفظ</button>
          </div>
        ) : null}
      </td>
      <td>
        {branchPrice == null ? "—" : formatPrice(branchPrice)}
        {branchPrice != null ? <div className="ownership-label">سعر هذا الفرع</div> : null}
        {branchEditable ? (
          <div className="inline-edit">
            <input aria-label={`سعر هذا الفرع ${price.priceName}`} value={branchDraft} onChange={(event) => setBranchDraft(event.target.value)} />
            <button type="button" onClick={() => void saveBranch()}>حفظ</button>
          </div>
        ) : null}
      </td>
    </tr>
  );
}

function UnitsTab({
  units,
  form,
  client,
  itemId,
  onSaved,
  onError,
}: {
  units: ItemUnits;
  form: ItemEditForm | null;
  client: ItemCatalogClient;
  itemId: string;
  onSaved: (message: string) => void;
  onError: (message: string) => void;
}) {
  const linked = form?.topology === "CentralLinked" || form?.topology === "Central";
  const masterEditable = Boolean(form?.canEditMaster && !form.masterReadOnly);
  const [factor, setFactor] = useState(units.units[0] ? String(units.units[0].unitsPerPackage) : "");
  const [internalNumber, setInternalNumber] = useState(units.units[0]?.internalUnitItemNumber ?? "");

  if (units.units.length === 0 && !masterEditable) {
    return (
      <div className="stack">
        {linked ? <span className="ownership-label">بيانات مركزية</span> : null}
        <p className="catalog-status">لا توجد وحدة داخلية مرتبطة بهذا الصنف</p>
      </div>
    );
  }

  async function saveUnits() {
    onError("");
    try {
      await client.saveUnits(itemId, {
        rowVersion: form?.rowVersion,
        units: [{
          unitsPerPackage: Number(factor),
          internalUnitItemNumber: internalNumber,
          internalUnitItemName: units.units[0]?.recordedUnitName,
          isActive: true,
        }],
      });
      onSaved("تم حفظ الوحدات.");
    } catch (reason) {
      onError(reason instanceof Error ? reason.message : "تعذر حفظ الوحدات.");
    }
  }

  return (
    <div className="stack">
      {linked ? <span className="ownership-label">بيانات مركزية</span> : null}
      {!units.ownsPhysicalStock ? (
        <div className="owner-note">
          <p>مالك المخزون: {units.stockOwnerItemNumber} — {units.stockOwnerItemName}</p>
          <p>كل وحدة من هذا الصنف تستهلك {formatFactor(units.baseQuantityFactor)} وحدات من مالك المخزون</p>
        </div>
      ) : null}
      {units.units.map((unit) => (
        <dl className="details" key={`${unit.internalUnitItemNumber}-${unit.unitsPerPackage}`}>
          <Field label="عدد الوحدات" value={formatFactor(unit.unitsPerPackage)} />
          <Field label="رقم الوحدة الداخلية" value={blank(unit.internalUnitItemNumber)} />
          <Field label="اسم الوحدة المسجل" value={blank(unit.recordedUnitName)} />
          <Field label="اسم الصنف الحالي" value={blank(unit.currentItemName)} />
        </dl>
      ))}
      {masterEditable ? (
        <div className="edit-grid">
          <label>
            عدد الوحدات
            <input aria-label="عدد الوحدات في العبوة" value={factor} onChange={(event) => setFactor(event.target.value)} />
          </label>
          <label>
            رقم الوحدة الداخلية
            <input aria-label="رقم الوحدة الداخلية" value={internalNumber} onChange={(event) => setInternalNumber(event.target.value)} />
          </label>
          <button type="button" onClick={() => void saveUnits()}>حفظ الوحدات</button>
        </div>
      ) : null}
    </div>
  );
}

function InventoryTab({ inventory }: { inventory: ItemInventory }) {
  const status = inventoryStatus(inventory);
  return (
    <div className="stack">
      {inventory.isDerived ? <p className="owner-note">الكمية تُحتسب من مخزون الوحدة الأساسية</p> : null}
      {status ? <p className="catalog-status">{status}</p> : <p className="quantity-figure">{formatQuantity(inventory.onHandQuantity ?? 0)}</p>}
    </div>
  );
}

function MovementsTab({ movements }: { movements: ItemMovement[] }) {
  if (movements.length === 0) {
    return <p className="catalog-status">لا توجد حركات مخزون</p>;
  }

  return (
    <table className="data-table">
      <thead>
        <tr>
          <th>التاريخ</th>
          <th>نوع الحركة</th>
          <th>الكمية</th>
          <th>ملاحظات/مرجع</th>
        </tr>
      </thead>
      <tbody>
        {movements.map((movement) => (
          <tr key={`${movement.occurredAt}-${movement.movementName}-${movement.quantityDelta}`}>
            <td>{formatWhen(movement.occurredAt)}</td>
            <td>{movement.movementName}</td>
            <td>{formatQuantity(movement.quantityDelta)}</td>
            <td>{blank(movement.notes || movement.referenceType)}</td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

function Field({ label, value }: { label: string; value: string }) {
  return (
    <div>
      <dt>{label}</dt>
      <dd>{value}</dd>
    </div>
  );
}

function formatWhen(value: string): string {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) {
    return value;
  }

  const pad = (part: number) => String(part).padStart(2, "0");
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())} ${pad(date.getHours())}:${pad(date.getMinutes())}`;
}
