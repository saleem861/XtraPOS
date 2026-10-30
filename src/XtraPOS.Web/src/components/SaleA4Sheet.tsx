import { useState } from "react";
import { ArrowLeft, MessageCircle, Printer, QrCode, X } from "lucide-react";
import type { A4Document } from "../cart/saleA4";

export function InvoicePreview({
  document: sheet,
  shareUrl,
  branchName,
  stationName,
  userName,
  onPrint,
  onWhatsApp,
  onBack,
}: {
  document: A4Document;
  shareUrl: string | null;
  branchName: string;
  stationName: string;
  userName: string;
  onPrint: () => void;
  onWhatsApp: () => void;
  onBack: () => void;
}) {
  const [qrOpen, setQrOpen] = useState(false);
  const [copied, setCopied] = useState(false);
  const shareNote = sheet.draft
    ? "لا يمكن مشاركة أمر بيع غير مكتمل كفاتورة نهائية."
    : "مشاركة الفاتورة المكتملة غير متاحة: لا يوجد عنوان HTTPS عام، ولن يُعرض رمز أو رابط محلي.";
  const canShare = !sheet.draft && Boolean(shareUrl);

  async function copyLink() {
    if (!shareUrl) return;
    await navigator.clipboard.writeText(shareUrl);
    setCopied(true);
  }

  return (
    <section className="invoice-preview" dir="rtl" aria-label="معاينة الفاتورة والطباعة والمشاركة">
      <header className="invoice-preview-bar">
        <div className="invoice-preview-brand">
          <strong>XtraPOS</strong>
          <span>{branchName} · {stationName}</span>
          <span>{userName}</span>
        </div>
        <h1>معاينة الفاتورة والطباعة والمشاركة</h1>
        <button type="button" className="invoice-preview-back" onClick={onBack}><ArrowLeft aria-hidden="true" />رجوع</button>
      </header>
      <div className="invoice-preview-context">
        <span>المرجع: {sheet.reference || "لم يُخصَّص رقم فاتورة"}</span>
        <span>الزبون: {sheet.customerNumber} · {sheet.customerName}</span>
        <span>الحالة: {sheet.draft ? "مسودة غير مكتملة" : "فاتورة مكتملة"}</span>
        <span>النوع: {sheet.title}</span>
      </div>
      <div className="invoice-preview-toolbar">
        <button type="button" onClick={onPrint}><Printer aria-hidden="true" />طباعة A4</button>
        <button type="button" disabled={!canShare} title={canShare ? "مشاركة واتساب" : shareNote} onClick={onWhatsApp}><MessageCircle aria-hidden="true" />مشاركة واتساب</button>
        <button type="button" disabled={!canShare} title={canShare ? "QR Code" : shareNote} onClick={() => setQrOpen(true)}><QrCode aria-hidden="true" />QR Code</button>
        {!canShare ? <p>{shareNote}</p> : null}
      </div>
      <div className="invoice-preview-body">
        <aside className="invoice-preview-details" aria-label="تفاصيل المستند">
          <h2>تفاصيل المستند</h2>
          <dl>
            <div><dt>التاريخ</dt><dd>{sheet.when}</dd></div>
            <div><dt>الحالة</dt><dd>{sheet.draft ? "مسودة" : "مكتملة"}</dd></div>
            <div><dt>الزبون</dt><dd>{sheet.customerName}</dd></div>
            <div><dt>عدد الأصناف</dt><dd>{sheet.itemCount}</dd></div>
            <div><dt>الإجمالي</dt><dd>{sheet.total} ₪</dd></div>
          </dl>
        </aside>
      <div className="invoice-preview-stage">
        <article className="sale-a4" dir="rtl">
          <header>
            <h1>{sheet.businessName}</h1>
            <p>{sheet.branchName}</p>
            <h2>{sheet.title}</h2>
            {sheet.draft ? <p>هذه الوثيقة ليست فاتورة مرحّلة.</p> : null}
          </header>
          <dl>
            <div><dt>التاريخ</dt><dd>{sheet.when}</dd></div>
            <div><dt>المرجع</dt><dd>{sheet.reference || "لم يُخصَّص رقم فاتورة"}</dd></div>
            <div><dt>الموظف</dt><dd>{sheet.cashierName}</dd></div>
            <div><dt>رقم الزبون</dt><dd>{sheet.customerNumber}</dd></div>
            <div><dt>اسم الزبون</dt><dd>{sheet.customerName}</dd></div>
          </dl>
          <table>
            <thead>
              <tr>
                <th>#</th><th>رقم الصنف</th><th>اسم الصنف</th><th>الكمية</th><th>سعر الوحدة</th><th>الخصم</th><th>الإجمالي</th>
              </tr>
            </thead>
            <tbody>
              {sheet.lines.map((line, index) => (
                <tr key={`${line.number}-${index}`}>
                  <td>{index + 1}</td>
                  <td>{line.number}</td>
                  <td>{line.name}</td>
                  <td>{line.quantity}</td>
                  <td>{line.unitPrice}</td>
                  <td>{line.discount}</td>
                  <td>{line.total} ₪</td>
                </tr>
              ))}
            </tbody>
          </table>
          <footer>
            <p>إجمالي الأصناف: {sheet.itemCount}</p>
            <p>إجمالي الخصومات: {sheet.discountTotal} ₪</p>
            <p>الإجمالي النهائي: {sheet.total} ₪</p>
          </footer>
        </article>
      </div>
      </div>
      {qrOpen && shareUrl ? (
        <div className="pos-overlay" role="dialog" aria-label="QR Code">
          <div className="pos-modal invoice-qr">
            <h3>امسح الرمز لتحميل الفاتورة</h3>
            <p>{shareUrl}</p>
            <button type="button" onClick={() => void copyLink()}>{copied ? "تم النسخ" : "نسخ الرابط"}</button>
            <button type="button" onClick={() => setQrOpen(false)}><X aria-hidden="true" />إغلاق</button>
          </div>
        </div>
      ) : null}
    </section>
  );
}
