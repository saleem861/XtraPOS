import { useEffect, useMemo, useState } from "react";

/** قاعدة المالك: كل نوافذ وشاشات الجرد تعرض الأصناف بصفحات، 10 أو 25 أو 50 لكل صفحة. */
export const PAGE_SIZES = [10, 25, 50] as const;
export type PageSize = (typeof PAGE_SIZES)[number];

export function pageCount(total: number, size: number): number {
  return Math.max(1, Math.ceil(total / size));
}

export function pageSlice<T>(items: readonly T[], page: number, size: number): T[] {
  const last = pageCount(items.length, size);
  const current = Math.min(Math.max(page, 1), last);
  return items.slice((current - 1) * size, current * size);
}

/**
 * يقسم القائمة إلى صفحات. أي تغيير في resetKey (البحث، التبويب، الفلتر) أو في حجم الصفحة يعيد إلى الصفحة الأولى.
 * إذا قصرت القائمة (حذف أو تثبيت) تبقى الصفحة ضمن المدى.
 */
export function usePaging<T>(items: readonly T[], resetKey: string = "") {
  const [size, setSize] = useState<PageSize>(10);
  const [page, setPage] = useState(1);
  useEffect(() => { setPage(1); }, [resetKey, size]);
  const pages = pageCount(items.length, size);
  const current = Math.min(page, pages);
  const visible = useMemo(() => pageSlice(items, current, size), [items, current, size]);
  return { visible, page: current, pages, size, total: items.length, setPage, setSize };
}

export function Pager({ page, pages, size, total, setPage, setSize, label = "صنف" }: {
  page: number;
  pages: number;
  size: PageSize;
  total: number;
  setPage: (page: number) => void;
  setSize: (size: PageSize) => void;
  label?: string;
}) {
  if (total === 0) return null;
  return (
    <nav className="inventory-pager" aria-label="الصفحات">
      <button type="button" className="ghost-button" disabled={page <= 1} onClick={() => setPage(page - 1)}>السابق</button>
      <span aria-live="polite">صفحة {page} من {pages} · {total} {label}</span>
      <button type="button" className="ghost-button" disabled={page >= pages} onClick={() => setPage(page + 1)}>التالي</button>
      <label>
        لكل صفحة
        <select value={size} onChange={(event) => setSize(Number(event.target.value) as PageSize)} aria-label="عدد الأصناف لكل صفحة">
          {PAGE_SIZES.map((value) => <option key={value} value={value}>{value}</option>)}
        </select>
      </label>
    </nav>
  );
}
