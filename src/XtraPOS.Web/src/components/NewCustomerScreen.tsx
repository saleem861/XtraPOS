import { useEffect, useMemo, useRef, useState, type KeyboardEvent } from "react";
import { ArrowLeft, Pencil, QrCode, Save, Send, ShieldCheck, Tag } from "lucide-react";
import { SetupClientError } from "../api/client";
import { createCustomerClient, type CustomerWriteBody, type SpecialPriceRow } from "../api/customers";
import { createItemClient } from "../api/items";
import { createPricingClient } from "../api/pricing";
import { ExceptionalPricesScreen } from "./ExceptionalPricesScreen";
import { PasskeyQrPanel } from "./PasskeyQrPanel";
import { FullKeyboard } from "./FullKeyboard";
import {
  alternatePalestinianPrefix,
  canSaveCustomer,
  customerTypeDefault,
  customerTypeOrder,
  defaultCallingCode,
  destinationE164,
  displayCallingCode,
  exceptionalPriceSummary,
  fallbackCallingCodes,
  nextCustomerField,
  normalizeCallingCodes,
  otpCodeLength,
  prefixRetryAllowed,
  splitPastedCallingCode,
  usesDesktopCustomerKeyboard,
  whatsAppSendError,
  whatsAppLocalFromCustomerNumber,
  type CallingCodeOption,
} from "../customers/newCustomerForm";
import { creditActivationError, creditStatusState, creditSwitchState } from "../credit/creditPolicyForm";
import type { DeviceSession } from "../types/setup";

const countryFlags: Record<string, string> = {
  فلسطين: "🇵🇸",
  إسرائيل: "🇮🇱",
  الأردن: "🇯🇴",
  السعودية: "🇸🇦",
  الإمارات: "🇦🇪",
  مصر: "🇪🇬",
  لبنان: "🇱🇧",
  سوريا: "🇸🇾",
  العراق: "🇮🇶",
  الكويت: "🇰🇼",
  عمان: "🇴🇲",
  قطر: "🇶🇦",
  البحرين: "🇧🇭",
};

function countryOptionLabel(country: CallingCodeOption): string {
  const flag = countryFlags[country.name] ?? "";
  return `${flag ? `${flag} ` : ""}${country.name} ${displayCallingCode(country.code)}`;
}

function FieldCombo({ id, label, value, placeholder, options, onChange, onKeyDown, menuClass }: {
  id: string;
  label: string;
  value: string;
  placeholder: string;
  options: { value: string; label: string }[];
  onChange: (value: string) => void;
  onKeyDown?: (event: KeyboardEvent<HTMLButtonElement>) => void;
  menuClass?: string;
}) {
  const [open, setOpen] = useState(false);
  const selected = options.find((option) => option.value === value);
  return (
    <div className={`nc-combo ${menuClass ?? ""}`}>
      <button id={id} type="button" className="nc-combo-trigger" role="combobox" aria-expanded={open} aria-label={label} onClick={() => setOpen((current) => !current)} onKeyDown={onKeyDown}>
        <span>{selected?.label ?? placeholder}</span>
      </button>
      {open ? (
        <ul className="nc-combo-menu" role="listbox">
          {options.map((option) => (
            <li key={`${option.value}-${option.label}`}>
              <button type="button" role="option" title={option.label} aria-selected={option.value === value} onClick={() => { onChange(option.value); setOpen(false); }}>{option.label}</button>
            </li>
          ))}
        </ul>
      ) : null}
    </div>
  );
}

export function NewCustomerScreen({ session, onBack, onSaved, title = "إضافة زبون جديد", customerId, permissions = [], readOnly = false, hideCredit = false }: {
  session: DeviceSession;
  onBack: () => void;
  onSaved: (customerId: string) => void;
  title?: string;
  customerId?: string;
  permissions?: string[];
  readOnly?: boolean;
  hideCredit?: boolean;
}) {
  const customers = useMemo(() => createCustomerClient(session), [session]);
  const items = useMemo(() => createItemClient(session), [session]);
  const pricing = useMemo(() => createPricingClient(session), [session]);
  const [codes, setCodes] = useState<CallingCodeOption[]>(fallbackCallingCodes);
  const [classifications, setClassifications] = useState<{ customerClassificationId: string; name: string }[]>([]);
  const [priceTypes, setPriceTypes] = useState<{ priceTypeId: string; priceCode: string; priceName: string }[]>([]);
  const [customerNumber, setCustomerNumber] = useState("");
  const [name, setName] = useState("");
  const [whatsAppLocal, setWhatsAppLocal] = useState("");
  const [whatsAppEdited, setWhatsAppEdited] = useState(false);
  const [countryCode, setCountryCode] = useState(defaultCallingCode);
  const [classificationId, setClassificationId] = useState("");
  const [priceTypeId, setPriceTypeId] = useState("");
  const [discount, setDiscount] = useState("");
  const [notes, setNotes] = useState("");
  const [recordVersion, setRecordVersion] = useState("");
  const [verifiedDestination, setVerifiedDestination] = useState<string | null>(null);
  const [originalDestination, setOriginalDestination] = useState<string | null>(null);
  const [otp, setOtp] = useState(() => Array.from({ length: otpCodeLength }, () => ""));
  const [verificationId, setVerificationId] = useState<string | null>(null);
  const [resendAt, setResendAt] = useState(0);
  const [now, setNow] = useState(Date.now());
  const [message, setMessage] = useState("");
  const [passkeyOpen, setPasskeyOpen] = useState(false);
  const [specials, setSpecials] = useState<SpecialPriceRow[]>([]);
  const [specialsOpen, setSpecialsOpen] = useState(false);
  const [desktopKeyboard, setDesktopKeyboard] = useState(usesDesktopCustomerKeyboard);
  const [savedCustomerId, setSavedCustomerId] = useState(customerId);
  const [creditEnabled, setCreditEnabled] = useState(false);
  const [creditLimit, setCreditLimit] = useState("0");
  const [paymentTermDays, setPaymentTermDays] = useState("30");
  const [creditStatus, setCreditStatus] = useState("Inactive");
  const [creditVersion, setCreditVersion] = useState("");
  const [creditOutstanding, setCreditOutstanding] = useState(0);
  const [creditAvailable, setCreditAvailable] = useState(!customerId);
  const [creditNotice, setCreditNotice] = useState("");
  const [creditAvailableAmount, setCreditAvailableAmount] = useState<number | null>(null);

  useEffect(() => {
    if (desktopKeyboard || !window.visualViewport) return undefined;
    const viewport = window.visualViewport;
    const apply = () => {
      const inset = Math.max(0, window.innerHeight - viewport.height - viewport.offsetTop);
      document.documentElement.style.setProperty("--keyboard-inset", `${inset}px`);
    };
    viewport.addEventListener("resize", apply);
    viewport.addEventListener("scroll", apply);
    return () => {
      viewport.removeEventListener("resize", apply);
      viewport.removeEventListener("scroll", apply);
      document.documentElement.style.removeProperty("--keyboard-inset");
    };
  }, [desktopKeyboard]);
  const canManageCredit = permissions.includes("System.Administer") || permissions.includes("Credit.ManageLimit");
  const canSuspendCredit = canManageCredit || permissions.includes("Credit.Suspend");

  useEffect(() => {
    void customers.classifications().then(setClassifications).catch(() => setMessage("تعذر تحميل أنواع الزبائن."));
    void customers.priceTypes().then(setPriceTypes).catch(() => setMessage("تعذر تحميل التسعيرات."));
    void customers.callingCodes().then((rows) => setCodes(normalizeCallingCodes(rows))).catch(() => setMessage("تعذر تحميل القائمة الدولية. +972 و +970 متاحتان."));
    if (customerId) {
      void customers.get(customerId).then((row) => {
        if (!row) return;
        setCustomerNumber(row.customerNumber ?? "");
        setName(row.name ?? "");
        setClassificationId(row.customerClassificationId ?? "");
        setPriceTypeId(row.priceTypeId ?? "");
        setDiscount(row.variableValueDiscountPercent);
        setNotes(row.notes ?? "");
        setRecordVersion(row.rowVersion ?? "");
        const phone = row.whatsAppPhoneE164 || row.phone || "";
        const split = splitPastedCallingCode(phone, codes);
        const loadedCode = split?.countryCode ?? countryCode;
        const loadedLocal = split ? split.localNumber : phone.replace(/\D/g, "");
        if (split) setCountryCode(split.countryCode);
        if (loadedLocal) setWhatsAppLocal(loadedLocal);
        setOriginalDestination(destinationE164(loadedCode, loadedLocal));
        if (row.phoneVerificationStatus === "Verified" && row.whatsAppPhoneE164) setVerifiedDestination(row.whatsAppPhoneE164);
      }).catch(() => setMessage("تعذر تحميل بيانات الزبون."));
      void customers.specialPrices(customerId).then(setSpecials).catch(() => setSpecials([]));
      void customers.creditPolicy(customerId).then((policy) => {
        setCreditEnabled(policy.creditEnabled);
        setCreditLimit(String(policy.creditLimit));
        setPaymentTermDays(String(policy.paymentTermDays));
        setCreditStatus(policy.creditStatus);
        setCreditVersion(policy.rowVersion);
        setCreditOutstanding(policy.outstanding);
        setCreditAvailableAmount(policy.available);
        setCreditAvailable(policy.centralReachable);
        setCreditNotice(policy.centralReachable ? "" : "تعذر الاتصال بجهة الائتمان المركزية.");
      }).catch((error: unknown) => {
        setCreditAvailable(false);
        setCreditAvailableAmount(null);
        setCreditNotice(error instanceof SetupClientError ? error.message : "تعذر الاتصال بجهة الائتمان المركزية.");
      });
    }
  }, [customers, customerId]);

  useEffect(() => {
    const timer = window.setInterval(() => setNow(Date.now()), 1000);
    return () => window.clearInterval(timer);
  }, []);

  useEffect(() => {
    if (typeof window.matchMedia !== "function") return;
    const query = window.matchMedia("(pointer: fine) and (hover: hover)");
    const apply = () => setDesktopKeyboard(query.matches);
    apply();
    query.addEventListener("change", apply);
    window.addEventListener("resize", apply);
    return () => {
      query.removeEventListener("change", apply);
      window.removeEventListener("resize", apply);
    };
  }, []);

  const destination = destinationE164(countryCode, whatsAppLocal);
  const saveEnabled = !readOnly && canSaveCustomer({ verifiedDestination, originalDestination, countryCode, whatsAppLocal, name, classificationId, priceTypeId });
  const wait = Math.max(0, Math.ceil((resendAt - now) / 1000));
  const orderedTypes = customerTypeOrder
    .map((label) => classifications.find((row) => row.name === label))
    .filter((row): row is { customerClassificationId: string; name: string } => Boolean(row));
  const shownTypes = orderedTypes.length > 0 ? orderedTypes : classifications;

  function focusField(id: string | null) {
    if (!id) return;
    const field = document.getElementById(id);
    field?.focus();
    if (field && !desktopKeyboard && typeof field.scrollIntoView === "function") field.scrollIntoView({ block: "center" });
  }

  function advance(current: string) {
    focusField(nextCustomerField(current));
  }

  function onEnter(event: KeyboardEvent<HTMLElement>, current: string) {
    if (event.key !== "Enter") return;
    event.preventDefault();
    advance(current);
  }

  function changeCustomerNumber(value: string) {
    setCustomerNumber(value);
    if (!whatsAppEdited) {
      setWhatsAppLocal(whatsAppLocalFromCustomerNumber(value));
      setVerifiedDestination(null);
    }
  }

  function changeWhatsApp(value: string) {
    const pasted = splitPastedCallingCode(value, codes);
    if (pasted) {
      setCountryCode(pasted.countryCode);
      setWhatsAppLocal(pasted.localNumber);
    } else {
      setWhatsAppLocal(value.replace(/\D/g, ""));
    }
    setWhatsAppEdited(value.trim() !== "");
    setVerifiedDestination(null);
  }

  function changeType(id: string) {
    setClassificationId(id);
    const selected = classifications.find((row) => row.customerClassificationId === id);
    const defaults = selected ? customerTypeDefault(selected.name, priceTypes) : null;
    if (defaults) {
      setPriceTypeId(defaults.priceTypeId);
      setDiscount(defaults.discount);
    }
  }

  function body(): CustomerWriteBody {
    const parsed = Number(discount);
    return {
      customerNumber,
      name,
      phone: whatsAppLocal,
      whatsAppCountryCode: countryCode,
      whatsAppConsent: false,
      customerClassificationId: classificationId,
      priceTypeId,
      notes,
      rowVersion: recordVersion || undefined,
      verificationId: verificationId ?? undefined,
      variableValueDiscountPercent: Number.isFinite(parsed) ? parsed : undefined,
      specialPrices: customerId ? undefined : specials.filter((row) => row.isActive).map((row) => ({
        itemNumber: row.itemNumber,
        minimumQuantity: row.minimumQuantity,
        bundlePrice: row.bundlePrice,
      })),
    };
  }

  async function send(country = countryCode, usedFallback = false) {
    setMessage("");
    const destinationForSend = destinationE164(country, whatsAppLocal);
    if (!customerNumber.trim() || !name.trim() || !classificationId || !priceTypeId || !destinationForSend) {
      setMessage("يرجى إكمال رقم الزبون والاسم ورقم الهاتف ونوع الزبون والتسعيرة.");
      return;
    }
    try {
      const sent = await customers.sendRegistration(customerNumber.trim(), destinationForSend);
      setCountryCode(country);
      setVerificationId(sent.verificationId);
      setResendAt(Date.parse(sent.resendAvailableAt));
      setMessage(`تم إرسال رمز التحقق إلى ${sent.maskedDestination}`);
    } catch (error) {
      const text = error instanceof SetupClientError ? error.message : "";
      const alternate = alternatePalestinianPrefix(country);
      if (prefixRetryAllowed(country, text, usedFallback) && alternate) {
        setMessage("تعذر الإرسال. تجري محاولة المقدمة البديلة.");
        await send(alternate, true);
        return;
      }
      setMessage(whatsAppSendError(text));
    }
  }

  async function verify() {
    if (!verificationId || !destination) return;
    try {
      await customers.verifyRegistration(verificationId, destination, otp.join(""));
      setVerifiedDestination(destination);
      setMessage("تم التحقق من رقم الهاتف بنجاح");
    } catch (error) {
      setMessage(error instanceof SetupClientError ? error.message : "رمز التحقق غير صحيح.");
    }
  }

  function fillOtp(index: number, value: string) {
    const digits = value.replace(/\D/g, "");
    if (digits.length > 1) {
      const next = digits.slice(0, otpCodeLength).split("");
      setOtp([...next, ...Array.from({ length: otpCodeLength }, () => "")].slice(0, otpCodeLength));
      return;
    }
    const next = [...otp];
    next[index] = digits;
    setOtp(next);
    const boxes = document.querySelectorAll<HTMLInputElement>(".new-customer-otp input");
    if (digits && boxes[index + 1]) boxes[index + 1].focus();
  }

  async function saveCredit(customerIdToSave: string, activation: { enabled: boolean; status: "Active" | "Inactive" | "Suspended" }, limit: number, terms: number) {
    const policy = await customers.saveCreditPolicy(customerIdToSave, {
      creditEnabled: activation.enabled,
      creditLimit: limit,
      paymentTermDays: terms,
      creditStatus: activation.status,
      rowVersion: creditVersion || undefined,
      reason: "customer-screen",
    });
    setCreditEnabled(policy.creditEnabled);
    setCreditLimit(String(policy.creditLimit));
    setPaymentTermDays(String(policy.paymentTermDays));
    setCreditStatus(policy.creditStatus);
    setCreditVersion(policy.rowVersion);
    setCreditOutstanding(policy.outstanding);
    setCreditAvailableAmount(policy.available);
    setCreditAvailable(policy.centralReachable);
    setCreditNotice(policy.centralReachable ? "" : "تعذر الاتصال بجهة الائتمان المركزية.");
  }

  async function save() {
    const activation = creditSwitchState(creditEnabled, creditStatus);
    const limit = Number(creditLimit);
    const terms = Number(paymentTermDays);
    const changed = activation.enabled || limit > 0 || terms !== 30 || activation.status !== "Inactive" || Boolean(customerId);
    if (canManageCredit && changed) {
      const problem = creditActivationError({ enabled: activation.enabled, limit: creditLimit, paymentTermDays, status: activation.status });
      if (problem) {
        setMessage(problem);
        return;
      }
    }
    if (readOnly) {
      if (!customerId || !canManageCredit) return;
      try {
        await saveCredit(customerId, activation, limit, terms);
        setMessage("تم حفظ إعدادات الاستدانة.");
      } catch (error) {
        setMessage(error instanceof SetupClientError ? error.message : "تعذر حفظ إعدادات الاستدانة.");
      }
      return;
    }
    if (!saveEnabled) return;
    if (!customerId && !verificationId) return;
    try {
      const saved = await customers.saveCustomer(body(), savedCustomerId);
      setSavedCustomerId(saved.customerId);
      if (canManageCredit && changed) {
        try {
          await saveCredit(saved.customerId, activation, limit, terms);
        } catch (error) {
          setMessage(error instanceof SetupClientError ? `تم حفظ الزبون. ${error.message}` : "تم حفظ الزبون. تعذر حفظ إعدادات الائتمان وتحتاج إلى مراجعة.");
          return;
        }
      }
      onSaved(saved.customerId);
    } catch (error) {
      setMessage(error instanceof SetupClientError ? error.message : "تعذر حفظ الزبون.");
    }
  }

  async function commitSpecial(row: { item: { itemId: string; itemNumber: string; itemName: string }; minimumQuantity: string; bundlePrice: string; targetUnitPrice: string; existing?: SpecialPriceRow }) {
    const minimumQuantity = Number(row.minimumQuantity);
    const bundlePrice = Number(row.bundlePrice);
    const specialPrice = Number(row.targetUnitPrice);
    if (customerId) {
      await customers.saveSpecialPrice(customerId, {
        itemNumber: row.item.itemNumber,
        specialPrice,
        minimumQuantity,
        bundlePrice,
        priceId: row.existing?.customerItemSpecialPriceId,
        rowVersion: row.existing?.rowVersion,
      });
      setSpecials(await customers.specialPrices(customerId));
      return;
    }
    const draft: SpecialPriceRow = {
      customerItemSpecialPriceId: row.existing?.customerItemSpecialPriceId ?? crypto.randomUUID(),
      itemId: row.item.itemId,
      itemNumber: row.item.itemNumber,
      itemName: row.item.itemName,
      basePrice: row.existing?.basePrice ?? 0,
      specialPrice,
      minimumQuantity,
      bundlePrice,
      validFrom: null,
      validTo: null,
      isActive: true,
      rowVersion: "",
    };
    setSpecials((current) => {
      const rest = current.filter((item) => item.itemNumber !== draft.itemNumber);
      return [...rest, draft];
    });
  }

  const searchEnter = useRef<() => void>(() => undefined);

  return (
    <section className={desktopKeyboard ? "new-customer full-keyboard-screen has-keyboard" : "new-customer"} dir="rtl" aria-label={specialsOpen ? "الأسعار الاستثنائية للزبون" : title}>
      <header className="new-customer-bar">
        <strong className="new-customer-brand">XtraPOS</strong>
        <h2>{specialsOpen ? "الأسعار الاستثنائية للزبون" : title}</h2>
        <button type="button" className="new-customer-back" onClick={specialsOpen ? () => setSpecialsOpen(false) : onBack}><ArrowLeft aria-hidden="true" />رجوع</button>
      </header>
      {specialsOpen ? (
        <ExceptionalPricesScreen
          customerNumber={customerNumber}
          customerName={name}
          rows={specials}
          onSearch={async (term) => (await items.loadItems({ search: term, activity: "active", page: 1, pageSize: 12, sort: "number" })).items}
          currentPrice={async (item) => (await pricing.resolveFixed(item.itemId, customerId ?? null, item.itemNumber, item.itemName, "1", false)).unitPrice}
          onCommit={commitSpecial}
          onDelete={async (row) => {
            if (customerId && row.rowVersion) {
              await customers.saveSpecialPrice(customerId, { itemNumber: row.itemNumber, specialPrice: row.specialPrice, minimumQuantity: row.minimumQuantity, bundlePrice: row.bundlePrice, priceId: row.customerItemSpecialPriceId, rowVersion: row.rowVersion, isActive: false });
              setSpecials(await customers.specialPrices(customerId));
            } else {
              setSpecials((current) => current.filter((item) => item.customerItemSpecialPriceId !== row.customerItemSpecialPriceId));
            }
          }}
          onClose={() => setSpecialsOpen(false)}
          bindSearchEnter={(handler) => { searchEnter.current = handler; }}
        />
      ) : null}
      {specialsOpen ? null : <>
      <div className="new-customer-card">
        <fieldset className="customer-identity" disabled={readOnly}>
        <div className="new-customer-row">
          <label><span>رقم الزبون <i>*</i></span><input id="customer-number" aria-label="رقم الزبون" inputMode="tel" value={customerNumber} onChange={(event) => changeCustomerNumber(event.target.value)} onKeyDown={(event) => onEnter(event, "customer-number")} /></label>
          <label><span>اسم الزبون <i>*</i></span><input id="customer-name" aria-label="اسم الزبون" value={name} onChange={(event) => setName(event.target.value)} onKeyDown={(event) => onEnter(event, "customer-name")} /></label>
          <label><span>رقم الهاتف للتحقق <i>*</i></span><input id="customer-phone" aria-label="رقم الهاتف للتحقق" inputMode="tel" value={whatsAppLocal} onChange={(event) => changeWhatsApp(event.target.value)} onKeyDown={(event) => onEnter(event, "customer-phone")} /></label>
          <label className="new-customer-country"><span>مقدمة الدولة</span>
            <FieldCombo id="customer-country" label="مقدمة الدولة" value={countryCode} placeholder="اختر" options={codes.map((country) => ({ value: country.code, label: countryOptionLabel(country) }))} onChange={(code) => { setCountryCode(code); setVerifiedDestination(null); }} onKeyDown={(event) => onEnter(event, "customer-country")} />
          </label>
        </div>
        <div className="new-customer-row is-second">
          <label className="is-type"><span>نوع الزبون <i>*</i></span>
            <FieldCombo id="customer-type" label="نوع الزبون" value={classificationId} placeholder="اختر" options={shownTypes.map((row) => ({ value: row.customerClassificationId, label: row.name }))} onChange={changeType} onKeyDown={(event) => onEnter(event, "customer-type")} />
          </label>
          <label className="is-price"><span>نوع التسعيرة (كود النظام)</span>
            <FieldCombo id="customer-price" label="نوع التسعيرة (كود النظام)" value={priceTypeId} placeholder="اختر" options={priceTypes.map((row) => ({ value: row.priceTypeId, label: row.priceCode }))} onChange={setPriceTypeId} />
          </label>
          <label className="is-discount"><span>نسبة الخصم</span><span className="discount-field"><input id="customer-discount" aria-label="نسبة الخصم" inputMode="decimal" value={discount} onChange={(event) => setDiscount(event.target.value.replace(/[^\d.]/g, ""))} /><Pencil aria-hidden="true" />{discount ? <b>%</b> : null}</span></label>
          <label className="new-customer-notes"><span>ملاحظات</span><input id="customer-notes" aria-label="ملاحظات" placeholder="ملاحظات إضافية عن الزبون ..." value={notes} onChange={(event) => setNotes(event.target.value)} onKeyDown={(event) => onEnter(event, "customer-notes")} /></label>
        </div>
        </fieldset>
        {hideCredit ? null : (
        <fieldset className="credit-settings" aria-label="إعدادات الائتمان والديون">
          <legend>إعدادات الائتمان والديون</legend>
          <label className={creditEnabled ? "credit-toggle is-on" : "credit-toggle"}>
            <span>السماح بالبيع بالدين</span>
            <span className="credit-switch">
              <input aria-label="السماح بالبيع بالدين" role="switch" type="checkbox" checked={creditEnabled} disabled={!canManageCredit} onChange={(event) => {
                const next = creditSwitchState(event.target.checked, creditStatus);
                setCreditEnabled(next.enabled);
                setCreditStatus(next.status);
              }} />
              <span className="credit-switch-track" aria-hidden="true">
                <span className="credit-switch-on">ON</span>
                <span className="credit-switch-off">OFF</span>
                <span className="credit-switch-knob" />
              </span>
            </span>
          </label>
          <label><span>الحد الأقصى للاستدانة</span><input aria-label="الحد الأقصى للاستدانة" inputMode="decimal" value={creditLimit} disabled={!canManageCredit} onChange={(event) => setCreditLimit(event.target.value.replace(/[^\d.]/g, ""))} onFocus={(event) => event.currentTarget.scrollIntoView({ block: "nearest" })} /></label>
          <label><span>مدة السداد الافتراضية</span><input aria-label="مدة السداد الافتراضية" inputMode="numeric" value={paymentTermDays} disabled={!canManageCredit} onChange={(event) => setPaymentTermDays(event.target.value.replace(/\D/g, ""))} onFocus={(event) => event.currentTarget.scrollIntoView({ block: "nearest" })} /></label>
          <label><span>حالة الحساب الائتماني</span>
            <FieldCombo id="credit-status" label="حالة الحساب الائتماني" value={creditEnabled ? "Active" : creditStatus === "Suspended" ? "Suspended" : "Inactive"} placeholder="غير مفعّل" options={[{ value: "Inactive", label: "غير مفعّل" }, { value: "Active", label: "مفعّل" }, { value: "Suspended", label: "موقوف" }]} onChange={(value) => {
              if (!canSuspendCredit) return;
              const next = creditStatusState(value);
              if (!next) return;
              if (!canManageCredit && next.status !== "Suspended") return;
              setCreditEnabled(next.enabled);
              setCreditStatus(next.status);
            }} />
          </label>
          <p>{creditAvailable ? `الرصيد القائم ${creditOutstanding} — المتاح ${creditAvailableAmount ?? "—"}` : "الرصيد القائم والمتاح غير معروفين."}</p>
          {creditNotice ? <p className="status-error">{creditNotice}</p> : null}
          {customerId ? <button type="button" onClick={() => {
            setCreditNotice("");
            void customers.creditPolicy(customerId).then((policy) => {
              setCreditEnabled(policy.creditEnabled);
              setCreditLimit(String(policy.creditLimit));
              setPaymentTermDays(String(policy.paymentTermDays));
              setCreditStatus(policy.creditStatus);
              setCreditVersion(policy.rowVersion);
              setCreditOutstanding(policy.outstanding);
              setCreditAvailableAmount(policy.available);
              setCreditAvailable(policy.centralReachable);
              setCreditNotice("");
            }).catch((error: unknown) => {
              setCreditAvailable(false);
              setCreditAvailableAmount(null);
              setCreditNotice(error instanceof SetupClientError ? error.message : "تعذر الاتصال بجهة الائتمان المركزية.");
            });
          }}>إعادة المحاولة</button> : null}
          {readOnly && customerId && canManageCredit ? <button type="button" onClick={() => void save()}>حفظ الاستدانة</button> : null}
        </fieldset>
        )}
        <fieldset className="customer-identity" disabled={readOnly}>
        {readOnly ? null : (
        <div className="new-customer-specials">
          <button className="specials-button" type="button" onClick={() => setSpecialsOpen(true)}><Tag aria-hidden="true" />إضافة / تعديل الأصناف الاستثنائية</button>
          <p>{exceptionalPriceSummary(specials)}</p>
        </div>
        )}
        {message ? <p className="new-customer-message">{message}</p> : null}
        {verifiedDestination ? <p className="new-customer-success">تم التحقق من رقم الهاتف بنجاح</p> : null}
        </fieldset>
      </div>
      {!readOnly && (customerId || savedCustomerId) ? (
        <div className="passkey-qr-row">
          <button id="passkey-qr" className="passkey-qr-button" type="button" onClick={() => setPasskeyOpen(true)}><QrCode aria-hidden="true" />تفعيل Passkey</button>
          {passkeyOpen ? <PasskeyQrPanel session={session} customerId={(customerId || savedCustomerId) as string} purpose="enrollment" canReplace={permissions.includes("Customers.Manage") || permissions.includes("System.Administer")} onClose={() => setPasskeyOpen(false)} /> : null}
        </div>
      ) : null}
      {readOnly ? null : (
      <div className="new-customer-actions">
        <button id="send-code" className="send-button" type="button" onClick={() => void send()} onKeyDown={(event) => onEnter(event, "send-code")}><Send aria-hidden="true" />إرسال رمز التحقق</button>
        <div className="new-customer-otp" dir="ltr">
          {otp.map((digit, index) => (
            <input id={index === 0 ? "otp-0" : undefined} key={index} aria-label={`رمز ${index + 1}`} inputMode="numeric" maxLength={otpCodeLength} value={digit} onChange={(event) => fillOtp(index, event.target.value)} onKeyDown={(event) => {
              if (event.key === "Enter") onEnter(event, "otp-0");
              if (event.key === "Backspace" && !otp[index] && index > 0) {
                document.querySelectorAll<HTMLInputElement>(".new-customer-otp input")[index - 1]?.focus();
              }
            }} />
          ))}
        </div>
        <button id="verify-code" className="verify-button" type="button" disabled={otp.join("").length !== otpCodeLength || !verificationId} onClick={() => void verify()} onKeyDown={(event) => onEnter(event, "verify-code")}><ShieldCheck aria-hidden="true" />التحقق من الرمز</button>
        <button className="resend-button" type="button" disabled={wait > 0 || !verificationId} onClick={() => void send()}>{wait > 0 ? `00:${String(wait).padStart(2, "0")}` : "إعادة الإرسال"}</button>
        <button id="save-customer" className="save-button" type="button" disabled={!saveEnabled} onClick={() => void save()}><Save aria-hidden="true" />{customerId ? "حفظ" : "حفظ الزبون"}</button>
      </div>
      )}
      </>}
      {desktopKeyboard ? <FullKeyboard onEnter={() => (specialsOpen ? searchEnter.current() : advance(document.activeElement?.id || ""))} /> : null}
    </section>
  );
}
