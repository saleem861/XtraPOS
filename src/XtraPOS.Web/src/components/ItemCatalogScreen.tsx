import { useEffect, useState } from "react";
import { blank } from "../catalog/format";
import type { ItemActivity, ItemCatalogClient, ItemListItem, ItemQuery, ItemSort } from "../catalog/types";
import { ScreenHeader } from "./ScreenHeader";
import { ItemDetailScreen } from "./ItemDetailScreen";

interface ItemCatalogScreenProps {
  client: ItemCatalogClient;
  onHome: () => void;
  debounceMs?: number;
}

export function ItemCatalogScreen({ client, onHome, debounceMs = 300 }: ItemCatalogScreenProps) {
  const [searchInput, setSearchInput] = useState("");
  const [activity, setActivity] = useState<ItemActivity>("active");
  const [sort, setSort] = useState<ItemSort>("number");
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(50);
  const [items, setItems] = useState<ItemListItem[]>([]);
  const [totalCount, setTotalCount] = useState(0);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const [selectedId, setSelectedId] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    const timer = window.setTimeout(() => {
      const query: ItemQuery = { search: searchInput.trim(), activity, page, pageSize, sort };
      setLoading(true);
      setError("");
      void client.loadItems(query)
      .then((result) => {
        if (cancelled) {
          return;
        }
        setItems(result.items);
        setTotalCount(result.totalCount);
      })
      .catch((failure: unknown) => {
        if (!cancelled) {
          console.error(failure);
          setError("تعذر تحميل الأصناف");
          setItems([]);
          setTotalCount(0);
        }
      })
      .finally(() => {
        if (!cancelled) {
          setLoading(false);
        }
      });
    }, debounceMs);
    return () => {
      cancelled = true;
      window.clearTimeout(timer);
    };
  }, [client, searchInput, activity, page, pageSize, sort, debounceMs]);

  if (selectedId) {
    return (
      <ItemDetailScreen
        client={client}
        itemId={selectedId}
        onHome={onHome}
        onBack={() => setSelectedId(null)}
      />
    );
  }

  const pageCount = Math.max(1, Math.ceil(totalCount / pageSize));

  return (
    <section className="catalog">
      <ScreenHeader title="الأصناف" onBack={onHome} />
      <div className="catalog-tools">
        <label className="catalog-search">
          <span className="sr-only">بحث</span>
          <input
            value={searchInput}
            placeholder="ابحث برقم الصنف أو الاسم"
            onChange={(event) => {
              setSearchInput(event.target.value);
              setPage(1);
            }}
          />
        </label>
        <label>
          الحالة
          <select
            aria-label="تصفية الحالة"
            value={activity}
            onChange={(event) => {
              setActivity(event.target.value as ItemActivity);
              setPage(1);
            }}
          >
            <option value="active">نشط</option>
            <option value="inactive">غير نشط</option>
            <option value="all">الكل</option>
          </select>
        </label>
        <label>
          الترتيب
          <select
            aria-label="الترتيب"
            value={sort}
            onChange={(event) => {
              setSort(event.target.value as ItemSort);
              setPage(1);
            }}
          >
            <option value="number">رقم الصنف</option>
            <option value="name">اسم الصنف</option>
          </select>
        </label>
        <label>
          الصفحة
          <select
            aria-label="حجم الصفحة"
            value={pageSize}
            onChange={(event) => {
              setPageSize(Number(event.target.value));
              setPage(1);
            }}
          >
            <option value={25}>25</option>
            <option value={50}>50</option>
            <option value={100}>100</option>
          </select>
        </label>
        <p className="result-count">{totalCount.toLocaleString("ar-EG")} صنف</p>
      </div>
      {error ? <p className="form-error">{error}</p> : null}
      {loading ? <p className="catalog-status">جاري التحميل</p> : null}
      {!loading && !error && items.length === 0 ? <p className="catalog-status">لا توجد أصناف مطابقة للبحث</p> : null}
      <div className="item-table-wrap">
        <table className="data-table item-table">
          <thead>
            <tr>
              <th>رقم الصنف</th>
              <th>اسم الصنف</th>
              <th>النوع</th>
              <th>الشركة</th>
              <th>التصنيف</th>
              <th>الحالة</th>
              <th>ممنوع البيع</th>
            </tr>
          </thead>
          <tbody>
            {items.map((item) => (
              <tr
                key={item.itemId}
                className={item.isActive ? "item-row" : "item-row is-inactive"}
                tabIndex={0}
                onClick={() => setSelectedId(item.itemId)}
                onKeyDown={(event) => {
                  if (event.key === "Enter") {
                    setSelectedId(item.itemId);
                  }
                }}
              >
                <td>{item.itemNumber}</td>
                <td>{item.itemName}</td>
                <td>{blank(item.itemType)}</td>
                <td>{blank(item.manufacturer)}</td>
                <td>{blank(item.classification)}</td>
                <td>{item.isActive ? "نشط" : "غير نشط"}</td>
                <td>{item.isProhibited ? "ممنوع البيع" : "—"}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      <div className="item-cards">
        {items.map((item) => (
          <button
            key={item.itemId}
            className={item.isActive ? "item-card" : "item-card is-inactive"}
            type="button"
            onClick={() => setSelectedId(item.itemId)}
          >
            <strong>{item.itemNumber}</strong>
            <span>{item.itemName}</span>
            <span>{blank(item.itemType)}</span>
            <span>{item.isActive ? "نشط" : "غير نشط"}</span>
            {item.isProhibited ? <span>ممنوع البيع</span> : null}
          </button>
        ))}
      </div>
      <div className="pager">
        <button type="button" onClick={() => setPage((current) => Math.max(1, current - 1))} disabled={page <= 1}>
          السابق
        </button>
        <span>صفحة {page} من {pageCount}</span>
        <button type="button" onClick={() => setPage((current) => current + 1)} disabled={page >= pageCount}>
          التالي
        </button>
      </div>
    </section>
  );
}
