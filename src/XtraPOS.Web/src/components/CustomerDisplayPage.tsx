import { useEffect, useState } from "react";
import QRCode from "qrcode";
import { customerDisplayChannel, THANKS_SECONDS, type CustomerDisplayState } from "../customerDisplay/customerDisplay";
import { membershipDisplayUpdate } from "../membership/membershipDisplay";
import { membershipDisplayChannel } from "./MembershipQr";
import "./customerDisplay.css";

const waiting: CustomerDisplayState = { phase: "idle", business: "", branch: "", memberQr: "" };

export function CustomerDisplayPage() {
  const [state, setState] = useState<CustomerDisplayState>(waiting);
  const [membership, setMembership] = useState<{ status: string; image: string } | null>(null);
  const [now, setNow] = useState(() => Date.now());
  const [fullscreen, setFullscreen] = useState(false);

  useEffect(() => {
    document.title = "شاشة الزبون";
    const channel = new BroadcastChannel(customerDisplayChannel);
    channel.onmessage = (event: MessageEvent<{ type?: string; state?: CustomerDisplayState }>) => {
      if (event.data?.type === "state" && event.data.state) setState(event.data.state);
    };
    const hello = () => channel.postMessage({ type: "hello" });
    hello();
    const timer = window.setInterval(hello, 5000);
    return () => {
      window.clearInterval(timer);
      channel.close();
    };
  }, []);

  useEffect(() => {
    const channel = new BroadcastChannel(membershipDisplayChannel);
    let stop = false;
    let hide = 0;
    channel.onmessage = (event: MessageEvent) => {
      const update = membershipDisplayUpdate(event.data ?? {});
      if (!update) return;
      window.clearTimeout(hide);
      if (update.status === "Pending" && update.url) {
        void QRCode.toDataURL(update.url, { margin: 1, width: 360 }).then((image) => { if (!stop) setMembership({ status: "Pending", image }); });
        return;
      }
      if (update.status === "Verified") {
        setMembership({ status: "Verified", image: "" });
        hide = window.setTimeout(() => setMembership(null), 4000);
        return;
      }
      setMembership(null);
    };
    const hello = () => channel.postMessage({ type: "hello" });
    hello();
    const timer = window.setInterval(hello, 2000);
    return () => {
      stop = true;
      window.clearInterval(timer);
      window.clearTimeout(hide);
      channel.close();
    };
  }, []);

  useEffect(() => {
    if (state.phase !== "thanks") return;
    const timer = window.setInterval(() => setNow(Date.now()), 1000);
    return () => window.clearInterval(timer);
  }, [state.phase]);

  useEffect(() => {
    const sync = () => setFullscreen(Boolean(document.fullscreenElement));
    document.addEventListener("fullscreenchange", sync);
    return () => document.removeEventListener("fullscreenchange", sync);
  }, []);

  const shown: CustomerDisplayState = state.phase === "thanks" && now - state.at > THANKS_SECONDS * 1000
    ? { phase: "idle", business: state.business, branch: state.branch, memberQr: "" }
    : state;

  return (
    <div className="cd-root" dir="rtl" lang="ar">
      {shown.phase === "idle" ? <Idle state={shown} /> : null}
      {shown.phase === "sale" ? <Sale state={shown} /> : null}
      {shown.phase === "payment" ? <Payment state={shown} /> : null}
      {shown.phase === "deferred" ? <Deferred state={shown} /> : null}
      {shown.phase === "thanks" ? <Thanks state={shown} /> : null}
      {membership ? (
        <div className="cd-member" role="dialog" aria-label="رمز العضوية">
          {membership.status === "Verified"
            ? <h2>تم التحقق من العضوية</h2>
            : <><h2>امسح رمز العضوية</h2>{membership.image ? <img src={membership.image} alt="رمز العضوية" width={360} height={360} /> : null}<p>افتح كاميرا هاتفك ووجّهها إلى الرمز</p></>}
        </div>
      ) : null}
      {!fullscreen ? (
        <button type="button" className="cd-fullscreen" onClick={() => void document.documentElement.requestFullscreen?.().catch(() => undefined)}>ملء الشاشة</button>
      ) : null}
    </div>
  );
}

function Bar({ business, branch, customerName }: { business: string; branch: string; customerName?: string | null }) {
  return (
    <header className="cd-bar">
      <div className="cd-brand"><strong>إكسترا</strong><span>{[business, branch].filter(Boolean).join(" — ")}</span></div>
      {customerName ? (
        <div className="cd-hello">
          <svg aria-hidden="true" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round"><circle cx="12" cy="8" r="4" /><path d="M4 21c1.5-4 4.5-6 8-6s6.5 2 8 6" /></svg>
          أهلًا {customerName}
        </div>
      ) : null}
    </header>
  );
}

function Idle({ state }: { state: Extract<CustomerDisplayState, { phase: "idle" }> }) {
  return (
    <section className="cd-idle" aria-label="ترحيب">
      <strong className="cd-logo">إكسترا</strong>
      <div className="cd-rule" />
      <h1>أهلًا وسهلًا</h1>
      {state.business || state.branch ? <p>{state.business}{state.branch ? ` — فرع ${state.branch}` : ""}</p> : null}
      {state.memberQr ? (
        <div className="cd-idle-qr">
          <img src={state.memberQr} alt="رمز الانضمام إلى العضوية" width={180} height={180} />
          <span>امسح الرمز للانضمام إلى عضوية المحل</span>
        </div>
      ) : null}
    </section>
  );
}

function Sale({ state }: { state: Extract<CustomerDisplayState, { phase: "sale" }> }) {
  const last = state.lines.find((line) => line.id === state.lastLineId) ?? state.lines[state.lines.length - 1];
  const visible = state.lines.slice(-8);
  return (
    <>
      <Bar business={state.business} branch={state.branch} customerName={state.customerName} />
      <main className="cd-sale">
        <section className="cd-items">
          {last ? (
            <div className="cd-last" aria-live="polite">
              <div><span>آخر صنف</span><strong>{last.name}</strong></div>
              <div className="cd-last-price"><span dir="ltr">{last.quantity} × {last.unitPrice} ₪</span><strong>{last.lineTotal} ₪</strong></div>
            </div>
          ) : null}
          <div className="cd-table" role="table" aria-label="أصناف الفاتورة">
            <div className="cd-row cd-head" role="row"><span role="columnheader">الصنف</span><span role="columnheader">الكمية</span><span role="columnheader">السعر</span><span role="columnheader">المجموع</span></div>
            {visible.map((line) => (
              <div key={line.id} className={line.id === state.lastLineId ? "cd-row is-last" : "cd-row"} role="row">
                <span role="cell" className="cd-name">{line.name}</span>
                <span role="cell">{line.quantity}</span>
                <span role="cell" className="cd-muted">{line.unitPrice} ₪</span>
                <span role="cell" className="cd-strong">{line.lineTotal} ₪</span>
              </div>
            ))}
          </div>
        </section>
        <aside className="cd-summary">
          <dl className="cd-facts">
            <div><dt>عدد القطع</dt><dd>{state.pieces}</dd></div>
            <div><dt>المجموع قبل الخصم</dt><dd>{state.beforeDiscount} ₪</dd></div>
            {state.discount !== "0.00" ? <div><dt>الخصم</dt><dd className="cd-discount">− {state.discount} ₪</dd></div> : null}
          </dl>
          <div className="cd-due"><span>المطلوب</span><strong>{state.total}</strong><span>شيكل</span></div>
        </aside>
      </main>
    </>
  );
}

function Payment({ state }: { state: Extract<CustomerDisplayState, { phase: "payment" }> }) {
  return (
    <>
      <Bar business={state.business} branch={state.branch} customerName={state.customerName} />
      <main className="cd-cards">
        <div className="cd-card"><span>المطلوب</span><strong>{state.total}</strong><small>شيكل</small></div>
        <div className="cd-card"><span>المدفوع</span><strong>{state.paid}</strong><small>{state.method === "Card" ? "بطاقة" : state.method === "Split" ? "نقدًا وبطاقة" : "نقدًا"}</small></div>
        <div className="cd-card is-accent"><span>الباقي لك</span><strong className="cd-huge">{state.change}</strong><small>شيكل</small></div>
      </main>
    </>
  );
}

function Deferred({ state }: { state: Extract<CustomerDisplayState, { phase: "deferred" }> }) {
  return (
    <>
      <Bar business={state.business} branch={state.branch} customerName={state.customerName} />
      <main className="cd-cards">
        <div className="cd-card"><span>المطلوب</span><strong>{state.total}</strong><small>شيكل</small></div>
        <div className="cd-card"><span>المدفوع الآن</span><strong>{state.paidNow}</strong><small>شيكل</small></div>
        <div className="cd-card is-accent"><span>المبلغ المؤجَّل</span><strong className="cd-huge">{state.deferred}</strong><small>شيكل</small></div>
      </main>
    </>
  );
}

function Thanks({ state }: { state: Extract<CustomerDisplayState, { phase: "thanks" }> }) {
  return (
    <main className="cd-thanks">
      <div className="cd-thanks-text">
        <div className="cd-check" aria-hidden="true">
          <svg width="52" height="52" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round"><path d="M5 12.5l4.5 4.5L19 7.5" /></svg>
        </div>
        <h1>شكرًا لزيارتكم</h1>
        <p>تم إتمام البيع. فاتورتك رقم {state.saleNumber}</p>
        {state.change !== "0.00" ? <p className="cd-change">الباقي لك: <strong>{state.change} ₪</strong></p> : null}
        <p>نتمنى لك يومًا سعيدًا</p>
      </div>
      {state.qr ? (
        <div className="cd-qr">
          <img src={state.qr} alt={state.qrLabel} width={280} height={280} />
          <strong>{state.qrLabel}</strong>
        </div>
      ) : null}
    </main>
  );
}
