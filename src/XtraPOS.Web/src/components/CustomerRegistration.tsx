import { useEffect, useState } from "react";
import { createCustomerClient } from "../api/customers";
import { SetupClientError } from "../api/client";
import type { DeviceSession } from "../types/setup";

export function CustomerRegistration({ session, onCreated, onClose }: {
  session: DeviceSession;
  onCreated: (customerId: string) => void;
  onClose: () => void;
}) {
  const customers = createCustomerClient(session);
  const [name, setName] = useState("");
  const [phone, setPhone] = useState("");
  const [country, setCountry] = useState("970");
  const [consent, setConsent] = useState(false);
  const [customerId, setCustomerId] = useState<string | null>(null);
  const [verificationId, setVerificationId] = useState<string | null>(null);
  const [masked, setMasked] = useState("");
  const [resendAt, setResendAt] = useState<number>(0);
  const [otp, setOtp] = useState("");
  const [verified, setVerified] = useState(false);
  const [message, setMessage] = useState("");
  const [itemNumber, setItemNumber] = useState("");
  const [specialPrice, setSpecialPrice] = useState("");
  const [prices, setPrices] = useState<Awaited<ReturnType<typeof customers.specialPrices>>>([]);
  const [now, setNow] = useState(Date.now());

  useEffect(() => {
    const timer = window.setInterval(() => setNow(Date.now()), 1000);
    return () => window.clearInterval(timer);
  }, []);

  useEffect(() => {
    const digits = phone.replace(/\D/g, "");
    if (digits.startsWith("00972") || (digits.startsWith("972") && digits.length > 10)) setCountry("972");
    if (digits.startsWith("00970") || digits.startsWith("970")) setCountry("970");
  }, [phone]);

  async function save() {
    setMessage("");
    try {
      const created = await customers.saveCustomer({
        customerNumber: phone,
        name,
        phone,
        whatsAppCountryCode: country,
        whatsAppConsent: consent,
        customerClassificationId: "",
        priceTypeId: "",
      });
      setCustomerId(created.customerId);
      if (!consent) {
        setMessage("تم تسجيل الزبون بدون إرسال واتساب.");
        return;
      }
      try {
        const sent = await customers.sendVerification(created.customerId);
        setVerificationId(sent.verificationId);
        setMasked(sent.maskedDestination);
        setResendAt(Date.parse(sent.resendAvailableAt));
      } catch (error) {
        setMessage(error instanceof SetupClientError ? error.message : "تم تسجيل الزبون، وتعذر إرسال واتساب.");
      }
    } catch (error) {
      setMessage(error instanceof SetupClientError ? error.message : "تعذر حفظ الزبون.");
    }
  }

  async function verify() {
    if (!customerId || !verificationId) return;
    try {
      await customers.verifyPhone(customerId, verificationId, otp);
      setVerified(true);
      setMessage("موثق عبر واتساب");
    } catch (error) {
      setMessage(error instanceof SetupClientError ? error.message : "رمز التحقق غير صحيح.");
    }
  }

  async function resend() {
    if (!customerId || now < resendAt) return;
    const sent = await customers.sendVerification(customerId);
    setVerificationId(sent.verificationId);
    setMasked(sent.maskedDestination);
    setResendAt(Date.parse(sent.resendAvailableAt));
  }

  async function loadPrices(id: string) {
    setPrices(await customers.specialPrices(id));
  }

  async function addPrice() {
    if (!customerId) return;
    await customers.saveSpecialPrice(customerId, { itemNumber, specialPrice: Number(specialPrice) });
    setItemNumber("");
    setSpecialPrice("");
    await loadPrices(customerId);
  }

  const wait = Math.max(0, Math.ceil((resendAt - now) / 1000));
  return (
    <section className="pos-customer-register" aria-label="تسجيل زبون">
      <h3>زبون جديد</h3>
      {!customerId ? (
        <>
          <input aria-label="اسم الزبون" placeholder="اسم الزبون" value={name} onChange={(event) => setName(event.target.value)} />
          <input aria-label="رقم الهاتف" dir="ltr" placeholder="05xxxxxxxx" value={phone} onChange={(event) => setPhone(event.target.value)} />
          <select aria-label="مقدمة الدولة" value={country} onChange={(event) => setCountry(event.target.value)}>
            <option value="970">+970</option>
            <option value="972">+972</option>
          </select>
          <label><input type="checkbox" checked={consent} onChange={(event) => setConsent(event.target.checked)} /> أوافق على استلام رمز التحقق عبر واتساب</label>
          <button className="primary-button" type="button" onClick={() => void save()}>حفظ</button>
        </>
      ) : (
        <>
          <p>{verified ? "موثق عبر واتساب" : "غير موثق"}</p>
          {verificationId && !verified ? (
            <>
              <p>تم إرسال رمز التحقق إلى واتساب</p>
              <p dir="ltr">{masked}</p>
              <input aria-label="رمز التحقق" inputMode="numeric" maxLength={6} value={otp} onChange={(event) => setOtp(event.target.value)} />
              <button className="primary-button" type="button" onClick={() => void verify()}>تحقق</button>
              <button className="nav-button" type="button" disabled={wait > 0} onClick={() => void resend()}>{wait > 0 ? `إعادة الإرسال ${wait}` : "إعادة الإرسال"}</button>
              <button className="nav-button" type="button" onClick={() => { setCustomerId(null); setVerificationId(null); }}>تغيير الرقم</button>
            </>
          ) : null}
          <h4>أسعار خاصة</h4>
          {prices.map((row) => (
            <p key={row.customerItemSpecialPriceId}>{row.itemNumber} {row.itemName} {row.basePrice} / {row.specialPrice} {row.isActive ? "فعال" : "ملغى"}</p>
          ))}
          <input aria-label="رقم الصنف" placeholder="رقم الصنف" value={itemNumber} onChange={(event) => setItemNumber(event.target.value)} />
          <input aria-label="السعر الخاص" placeholder="السعر الخاص" value={specialPrice} onChange={(event) => setSpecialPrice(event.target.value)} />
          <button className="nav-button" type="button" onClick={() => void addPrice()}>إضافة سعر خاص</button>
        </>
      )}
      {message ? <p>{message}</p> : null}
      {customerId ? <button className="primary-button" type="button" onClick={() => onCreated(customerId)}>اختيار الزبون</button> : null}
      <button className="nav-button" type="button" onClick={onClose}>إغلاق</button>
    </section>
  );
}
