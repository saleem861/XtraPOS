import { Check } from "lucide-react";

export interface BranchCardOption {
  branchId: string;
  branchName: string;
  /** سطر صغير تحت الاسم، مثل «فرع هذا الجهاز». */
  note?: string;
}

/**
 * اختيار فرع أو أكثر ببطاقات قابلة للتحديد، كما في النظام القديم (المالك 27 نوفمبر 2026).
 * مكوّن واحد لـ«معالجة النواقص» و«تحديد جرد»: النقر يحدد البطاقة أو يلغي تحديدها، والمحددة مظللة بعلامة ✓.
 */
export function BranchCardPicker({
  branches,
  selected,
  onToggle,
  onAll,
  label = "اختيار الفروع",
}: {
  branches: BranchCardOption[];
  selected: readonly string[];
  onToggle: (branchId: string) => void;
  /** يُظهر «تحديد الكل» و«إلغاء تحديد الكل» حين يُمرَّر. */
  onAll?: (selectAll: boolean) => void;
  label?: string;
}) {
  const all = branches.length > 0 && branches.every((branch) => selected.includes(branch.branchId));
  return (
    <div className="branch-card-picker">
      {onAll && branches.length > 1 ? (
        <div className="inventory-row-actions">
          <button type="button" className="ghost-button" disabled={all} onClick={() => onAll(true)}>تحديد الكل</button>
          <button type="button" className="ghost-button" disabled={selected.length === 0} onClick={() => onAll(false)}>إلغاء تحديد الكل</button>
        </div>
      ) : null}
      <div className="branch-card-grid" role="group" aria-label={label}>
        {branches.length === 0 ? <p className="inventory-hint">لا توجد فروع فعالة.</p> : null}
        {branches.map((branch) => {
          const picked = selected.includes(branch.branchId);
          return (
            <button
              key={branch.branchId}
              type="button"
              className={picked ? "branch-card is-selected" : "branch-card"}
              aria-pressed={picked}
              onClick={() => onToggle(branch.branchId)}
            >
              <span className="branch-card-check" aria-hidden="true">{picked ? <Check size={16} /> : null}</span>
              <strong>{branch.branchName}</strong>
              {branch.note ? <small>{branch.note}</small> : null}
            </button>
          );
        })}
      </div>
      <p className="inventory-hint" role="status">تم اختيار {selected.length} من {branches.length} فرع</p>
    </div>
  );
}
