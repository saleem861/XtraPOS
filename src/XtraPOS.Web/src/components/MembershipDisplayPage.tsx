import { useEffect, useState } from "react";
import { membershipDisplayUpdate } from "../membership/membershipDisplay";
import { membershipDisplayChannel } from "./MembershipQr";

export function MembershipDisplayPage({ counterId }: { counterId?: string }) {
  const [url, setUrl] = useState("");
  const [status, setStatus] = useState("");

  useEffect(() => {
    const channel = new BroadcastChannel(membershipDisplayChannel);
    const hello = () => channel.postMessage({ type: "hello", counterId });
    hello();
    const timer = window.setInterval(hello, 2000);
    channel.onmessage = (event: MessageEvent<{ type?: string; url?: string; status?: string; counterId?: string }>) => {
      const update = membershipDisplayUpdate(event.data ?? {}, counterId);
      if (!update) return;
      setStatus(update.status);
      setUrl(update.url);
    };
    return () => {
      window.clearInterval(timer);
      channel.close();
    };
  }, []);

  return (
    <section className="card" dir="rtl" aria-label="شاشة الزبون">
      <h1>{status === "Verified" ? "تم التحقق من العضوية" : "عضوية الزبون"}</h1>
      {status === "Verified" ? null : url ? <p dir="ltr">{url}</p> : <p>بانتظار رمز التحقق من نقطة البيع.</p>}
    </section>
  );
}
