import { ChevronLeft, ChevronRight } from "lucide-react";
import { useEffect, useMemo, useRef, useState, type CSSProperties } from "react";
import { createQuickItemsClient, quickItemHue, quickItemIcon, type QuickItem } from "../api/quickItems";
import type { DeviceSession } from "../types/setup";
import "./quickItems.css";

/**
 * شريط «الأصناف السريعة» في شاشة البيع: صف واحد يظهر منه 6 إلى 10 بطاقات، والباقي بالتمرير الأفقي.
 * يُقرأ من قاعدة الجهاز فقط مرة عند فتح الشاشة؛ لا يظهر إن كانت القائمة فارغة.
 */
export function QuickItemsStrip({ session, disabled, onPick }: {
  session: DeviceSession;
  disabled?: boolean;
  onPick: (itemNumber: string) => void;
}) {
  const client = useMemo(() => createQuickItemsClient(session), [session]);
  const [items, setItems] = useState<QuickItem[]>([]);
  const track = useRef<HTMLDivElement>(null);

  useEffect(() => {
    let stop = false;
    void client.current()
      .then((result) => { if (!stop) setItems(result.lines ?? []); })
      .catch(() => { if (!stop) setItems([]); });
    return () => { stop = true; };
  }, [client]);

  if (items.length === 0) return null;

  function scroll(direction: 1 | -1) {
    const node = track.current;
    if (!node) return;
    // RTL: التمرير «للأمام» يعني نحو اليسار.
    node.scrollBy({ left: -direction * node.clientWidth * 0.8, behavior: "smooth" });
  }

  return (
    <div className="quick-items" aria-label="الأصناف السريعة" onMouseDown={(event) => event.preventDefault()}>
      <button type="button" className="quick-items-nav" aria-label="الأصناف السابقة" onClick={() => scroll(-1)}><ChevronRight aria-hidden="true" /></button>
      <div className="quick-items-track" ref={track}>
        {items.map((item) => (
          <button
            key={item.itemNumber}
            type="button"
            className="quick-item"
            disabled={disabled}
            title={`${item.itemNumber} — ${item.itemName}`}
            style={{ "--quick-hue": String(quickItemHue(item.classification)) } as CSSProperties}
            onClick={() => onPick(item.itemNumber)}
          >
            <span className="quick-item-icon" aria-hidden="true">{quickItemIcon(item)}</span>
            <strong className="quick-item-number">{item.itemNumber}</strong>
            <span className="quick-item-name">{item.itemName}</span>
          </button>
        ))}
      </div>
      <button type="button" className="quick-items-nav" aria-label="الأصناف التالية" onClick={() => scroll(1)}><ChevronLeft aria-hidden="true" /></button>
    </div>
  );
}
