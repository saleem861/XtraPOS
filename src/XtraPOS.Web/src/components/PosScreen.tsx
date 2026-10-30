import { useEffect, useMemo, useRef, useState, type PointerEvent } from "react";
import { createPortal } from "react-dom";
import {
  Archive,
  ArrowDown,
  ArrowLeft,
  Banknote,
  Box,
  ChartNoAxesColumnIncreasing,
  CirclePlus,
  Clock3,
  Coins,
  FolderOpen,
  Keyboard,
  LockKeyhole,
  LogOut,
  Monitor,
  PackageSearch,
  Percent,
  Printer,
  ReceiptText,
  RefreshCw,
  Save,
  Search,
  ShoppingBag,
  ShoppingCart,
  Store,
  Tag,
  Trash2,
  ArrowUp,
  ArrowUpDown,
  CircleAlert,
  Undo2,
  UserRound,
  UserRoundSearch,
  WalletCards,
  X,
  type LucideIcon,
} from "lucide-react";
import { InvoicePreview } from "./SaleA4Sheet";
import { buildA4Document, publicHttpsBase, whatsAppShareUrl } from "../cart/saleA4";
import { HeldDraftsScreen } from "./HeldDraftsScreen";
import { CashSessionClientError, createCashSessionClient, isNonNegativeMoney, type CashSessionState } from "../api/cashSession";
import { CheckoutClientError, createCheckoutClient, type CompletedSale } from "../api/checkout";
import { createHeldSaleClient, HeldSaleClientError } from "../api/heldSales";
import { createItemClient } from "../api/items";
import { createCustomerClient, type CustomerListItem } from "../api/customers";
import { NewCustomerScreen } from "./NewCustomerScreen";
import { PasskeyQrPanel } from "./PasskeyQrPanel";
import { CreditSaleScreen } from "./CreditSaleScreen";
import { DebtAccountScreen } from "./DebtAccountScreen";
import { DebtRepaymentScreen } from "./DebtRepaymentScreen";
import { deviceHeaders } from "../api/client";
import { FullKeyboard } from "./FullKeyboard";
import { createPricingClient, PricingClientError } from "../api/pricing";
import { cartAfterSuccessfulHold, cartFromHeldSale, claimSingleHold, currentInvoiceNeedsGuard, holdSaveRequest } from "../cart/heldInvoice";
import { createIndexedDbDraftDatabase, loadDraft, publishCartRestoreView, recordCartStorageEvent, saveDraft } from "../cart/draftStore";
import { addDecimals, compareDecimals, formatPosMoney, isPositiveDecimal, isPositiveInteger, multiplyDecimals, roundDecimal, subtractDecimals } from "../cart/decimal";
import { interpretFastEntry } from "../cart/fastEntry";
import { POS_SEARCH_PAGE_SIZE, sortSearchResults, type SearchSortColumn } from "../cart/itemSearch";
import { sortCustomers, type CustomerSortColumn } from "../cart/customerSearch";
import {
  addFixedLine,
  addVariableLine,
  cartTotals,
  originalCartTotal,
  clearCart,
  clearPriceOverride,
  createCart,
  overrideLineTotal,
  overrideUnitPrice,
  removeLine,
  backfillOriginalPrices,
  repriceCart,
  updateFixedQuantity,
  updateVariableQuantity,
  type CartDraft,
  type CartScope,
  type FixedCartLine,
  type PricingGateway,
  type VariableCartLine,
} from "../cart/model";
import { NumericEditModal } from "./NumericEditModal";
import { NumericKeypad } from "./NumericKeypad";
import type { ItemListItem } from "../catalog/types";
import type { CurrentUser, DeviceSession } from "../types/setup";

interface PosScreenProps {
  session: DeviceSession;
  user: CurrentUser;
  onBack: () => void;
  onLogout?: () => void;
}

const QUICK_ACTIONS: { label: string; icon: LucideIcon; title: string }[] = [
  { label: "الديون", icon: WalletCards, title: "الديون" },
  { label: "إيصال", icon: ReceiptText, title: "إيصال" },
  { label: "استرجاع", icon: Undo2, title: "استرجاع" },
  { label: "المبيعات", icon: ChartNoAxesColumnIncreasing, title: "المبيعات" },
  { label: "الجرار", icon: Banknote, title: "درج النقدية" },
  { label: "تأجيل الدفع", icon: Clock3, title: "تأجيل الدفع" },
  { label: "شاشة", icon: Monitor, title: "شاشة الزبون" },
  { label: "فتح", icon: FolderOpen, title: "فتح" },
  { label: "مبيعات الزبون ؟", icon: UserRoundSearch, title: "مبيعات الزبون" },
];

function prefersProfessionalShell(): boolean {
  if (typeof window.matchMedia !== "function") return false;
  return window.matchMedia("(pointer: fine) and (hover: hover), (orientation: landscape) and (min-width: 1000px) and (min-height: 640px)").matches;
}

function appendMoney(current: string, digit: string, fresh = false): string {
  const base = fresh ? "" : current;
  if (digit === ".") {
    if (base.includes(".")) return base;
    return `${base || "0"}.`;
  }
  const next = `${base}${digit}`;
  const fraction = next.split(".")[1];
  if ((fraction?.length ?? 0) > 2) return base;
  return next.replace(/^0+(?=\d)/, "");
}

const ARABIC_KEY_ROWS = [
  ["1", "2", "3", "4", "5", "6", "7", "8", "9", "0"],
  ["ض", "ص", "ث", "ق", "ف", "غ", "ع", "ه", "خ", "ح", "ج"],
  ["ش", "س", "ي", "ب", "ل", "ا", "ت", "ن", "م", "ك", "ط"],
  ["ئ", "ء", "ؤ", "ر", "لا", "ى", "ة", "و", "ز", "ظ"],
];

const ENGLISH_KEY_ROWS = [
  ["1", "2", "3", "4", "5", "6", "7", "8", "9", "0"],
  ["q", "w", "e", "r", "t", "y", "u", "i", "o", "p"],
  ["a", "s", "d", "f", "g", "h", "j", "k", "l"],
  ["z", "x", "c", "v", "b", "n", "m"],
];

export function customerSyncLabel(state: string | null): string {
  switch (state) {
    case "Current": return "محدث";
    case "Offline": return "دون اتصال";
    case "Preparing": return "جارٍ التحضير";
    case "Failed": return "تعذرت المزامنة";
    default: return "";
  }
}

export function PosScreen({ session, user, onBack, onLogout }: PosScreenProps) {
  const [professionalShell, setProfessionalShell] = useState(prefersProfessionalShell);
  useEffect(() => {
    if (typeof window.matchMedia !== "function") return;
    const query = window.matchMedia("(pointer: fine) and (hover: hover), (orientation: landscape) and (min-width: 1000px) and (min-height: 640px)");
    const apply = () => setProfessionalShell(query.matches);
    apply();
    query.addEventListener("change", apply);
    return () => query.removeEventListener("change", apply);
  }, []);
  useEffect(() => {
    const active = professionalShell && document.getElementById("pos-topbar-slot") !== null;
    document.documentElement.classList.toggle("pos-pro-shell", active);
    return () => document.documentElement.classList.remove("pos-pro-shell");
  }, [professionalShell]);
  const scope: CartScope = {
    businessId: session.businessId,
    branchId: session.branchId,
    stationId: session.stationId,
    deviceId: session.deviceId,
    userId: user.userId,
  };
  const entryRef = useRef<HTMLInputElement>(null);
  const entryValueRef = useRef("");
  const [cart, setCart] = useState<CartDraft | null>(null);
  const [cartRestore, setCartRestore] = useState<"loading" | "ready" | "unavailable">("loading");
  const [entry, setEntry] = useState("");
  const [searchOpen, setSearchOpen] = useState(false);
  const [customerOpen, setCustomerOpen] = useState(false);
  const [registerOpen, setRegisterOpen] = useState(false);
  const [creditOpen, setCreditOpen] = useState(false);
  const [creditEnabled, setCreditEnabled] = useState(false);
  const [creditOperationId, setCreditOperationId] = useState("");
  const [collectionEnabled, setCollectionEnabled] = useState(false);
  const [reversalEnabled, setReversalEnabled] = useState(false);
  const [debtCustomer, setDebtCustomer] = useState<{ customerId: string; customerNumber: string | null; name: string; phone: string | null } | null>(null);
  const [debtRepay, setDebtRepay] = useState(false);
  useEffect(() => {
    let cancelled = false;
    void fetch("/api/pos/credit-sales/enabled", { headers: deviceHeaders(session) })
      .then((response) => response.ok ? response.json() as Promise<{ enabled?: boolean }> : { enabled: false })
      .then((body) => { if (!cancelled) setCreditEnabled(body.enabled === true); })
      .catch(() => { if (!cancelled) setCreditEnabled(false); });
    return () => { cancelled = true; };
  }, [session]);
  useEffect(() => {
    let cancelled = false;
    void fetch("/api/pos/credit-collections/enabled", { headers: deviceHeaders(session) })
      .then((response) => response.ok ? response.json() as Promise<{ enabled?: boolean }> : { enabled: false })
      .then((body) => { if (!cancelled) setCollectionEnabled(body.enabled === true); })
      .catch(() => { if (!cancelled) setCollectionEnabled(false); });
    return () => { cancelled = true; };
  }, [session]);
  useEffect(() => {
    let cancelled = false;
    void fetch("/api/pos/credit-reversals/enabled", { headers: deviceHeaders(session) })
      .then((response) => response.ok ? response.json() as Promise<{ enabled?: boolean }> : { enabled: false })
      .then((body) => { if (!cancelled) setReversalEnabled(body.enabled === true); })
      .catch(() => { if (!cancelled) setReversalEnabled(false); });
    return () => { cancelled = true; };
  }, [session]);
  const [editCustomerId, setEditCustomerId] = useState<string | undefined>();
  const [customerQuery, setCustomerQuery] = useState("");
  const [customerResults, setCustomerResults] = useState<CustomerListItem[]>([]);
  const [customerSyncState, setCustomerSyncState] = useState<string | null>(null);
  const [customerTotal, setCustomerTotal] = useState(0);
  const [customerPage, setCustomerPage] = useState(1);
  const [customerDone, setCustomerDone] = useState(false);
  const [customerLoading, setCustomerLoading] = useState(false);
  const [customerMessage, setCustomerMessage] = useState("");
  const [customerError, setCustomerError] = useState("");
  const [customerSelected, setCustomerSelected] = useState(-1);
  const [customerSort, setCustomerSort] = useState<{ column: CustomerSortColumn; direction: "asc" | "desc" } | null>(null);
  const [saleCustomer, setSaleCustomer] = useState<CustomerListItem | null>(null);
  const [passkeyVerify, setPasskeyVerify] = useState(false);
  const customerInputRef = useRef<HTMLInputElement>(null);
  const [searchSort, setSearchSort] = useState<{ column: SearchSortColumn; direction: "asc" | "desc" } | null>(null);
  const [searchFrame, setSearchFrame] = useState<{ top: number; height: number } | null>(null);
  const [search, setSearch] = useState("");
  const [searchDone, setSearchDone] = useState(false);
  const [selectedResult, setSelectedResult] = useState(-1);
  const [searchLang, setSearchLang] = useState<"ar" | "en">("ar");
  const [results, setResults] = useState<ItemListItem[]>([]);
  const [visibleResults, setVisibleResults] = useState(POS_SEARCH_PAGE_SIZE);
  const [searchPage, setSearchPage] = useState(1);
  const [searchTotal, setSearchTotal] = useState(0);
  const [message, setMessage] = useState("");
  const [missingItem, setMissingItem] = useState<string | null>(null);
  const lookupBusy = useRef(false);
  const suppressEntryFocus = useRef(false);
  const [connected, setConnected] = useState(true);
  const [confirmClear, setConfirmClear] = useState(false);
  const [quantityTarget, setQuantityTarget] = useState<string | null>(null);
  const [quantityDraft, setQuantityDraft] = useState("");
  const [quantityError, setQuantityError] = useState("");
  const [priceEdit, setPriceEdit] = useState<{ lineId: string; kind: "unit" | "total"; value: string } | null>(null);
  const [priceError, setPriceError] = useState("");
  const searchInputRef = useRef<HTMLInputElement>(null);
  const [cashSession, setCashSession] = useState<CashSessionState | null>(null);
  const [cashStatus, setCashStatus] = useState<"loading" | "ready" | "unavailable">("loading");
  const [cashMessage, setCashMessage] = useState("");
  const [opening, setOpening] = useState(false);
  const [closing, setClosing] = useState(false);
  const [openingAmount, setOpeningAmount] = useState("");
  const [countedAmount, setCountedAmount] = useState("");
  const [closedSummary, setClosedSummary] = useState<CashSessionState | null>(null);
  const [expectedPreview, setExpectedPreview] = useState<string | null>(null);
  const [closeBlocked, setCloseBlocked] = useState(false);
  const database = useMemo(() => createIndexedDbDraftDatabase(), []);
  const pricing = useMemo(() => createPricingClient(session), [session]);
  const customers = useMemo(() => createCustomerClient(session), [session]);
  const items = useMemo(() => createItemClient(session), [session]);
  const cashSessions = useMemo(() => createCashSessionClient(session), [session]);
  const checkoutClient = useMemo(() => createCheckoutClient(session), [session]);
  const heldSales = useMemo(() => createHeldSaleClient(session), [session]);
  const holdBusy = useRef({ busy: false });
  const [heldOpen, setHeldOpen] = useState(false);
  const [heldRestoring, setHeldRestoring] = useState(false);
  const [heldGuard, setHeldGuard] = useState<string | null>(null);
  const [completedShare, setCompletedShare] = useState<{ saleId: string; phone: string | null } | null>(null);
  const [printSheet, setPrintSheet] = useState<ReturnType<typeof buildA4Document>>(null);
  const [paying, setPaying] = useState(false);
  const [finePointer, setFinePointer] = useState(() => typeof window.matchMedia === "function" && window.matchMedia("(pointer: fine)").matches);
  const [desktopKeyboard, setDesktopKeyboard] = useState(() => typeof window.matchMedia === "function" && window.matchMedia("(pointer: fine) and (hover: hover)").matches);
  const [payField, setPayField] = useState<"tendered" | "cashPart">("tendered");
  const [payFresh, setPayFresh] = useState(true);
  const [payMethod, setPayMethod] = useState<"Cash" | "Card" | "Split">("Cash");
  const [tendered, setTendered] = useState("");
  const [cashPart, setCashPart] = useState("");
  const [checkoutPending, setCheckoutPending] = useState(false);
  const [receipt, setReceipt] = useState<CompletedSale | null>(null);
  const gesture = useRef({ x: 0, y: 0, moved: false, scrolled: false, cancelled: false });
  const cartRegionRef = useRef<HTMLDivElement>(null);

  const restoreRun = useRef(0);

  useEffect(() => {
    const runId = restoreRun.current + 1;
    restoreRun.current = runId;
    const run = `[run ${runId}]`;
    let effectCancelled = false;
    let resultApplied = false;
    const note = (event: string) => recordCartStorageEvent(`${run} ${event}`);
    note("restore-effect-start");
    const finish = (next: CartDraft, unavailable = false) => {
      note(`post-result-guard cancelled=${effectCancelled} settled=${resultApplied}`);
      if (effectCancelled) {
        note("post-result-cancelled-return");
        note("effect-cancelled-before-result");
        return;
      }
      if (resultApplied) {
        note("post-result-settled-return");
        return;
      }
      resultApplied = true;
      note("post-result-continue");
      if (unavailable) note("fallback-active");
      else if (next.lines.length === 0) note("empty-cart-created");
      else note("restored-cart-applied");
      note("cart-state-set");
      setCart(next);
      note(unavailable ? "cartRestore-set-unavailable" : "cartRestore-set-ready");
      setCartRestore(unavailable ? "unavailable" : "ready");
      if (unavailable) {
        setConnected(false);
        setMessage("تعذر الوصول إلى التخزين المحلي. ستعمل السلة مؤقتًا خلال هذه الجلسة.");
      }
    };
    const timer = window.setTimeout(() => finish(createCart(scope), true), 4000);
    void loadDraft(database, scope, run)
      .then(async (draft) => {
        window.clearTimeout(timer);
        note("effect-result-received");
        note("post-result-enter");
        if (!draft) {
          note("post-result-null-continue");
          finish(createCart(scope));
          return;
        }
        let hydrated = draft;
        try {
          hydrated = await backfillOriginalPrices(draft, (line) => pricing.resolveFixed(line.itemId, draft.customerId, line.itemNumber, line.itemName));
          if (hydrated !== draft) await saveDraft(database, hydrated);
        } catch {
          hydrated = draft;
        }
        finish(hydrated);
      })
      .catch(() => {
        window.clearTimeout(timer);
        if (resultApplied || effectCancelled) return;
        note("loadDraft-rejected");
        finish(createCart(scope), true);
      });
    void refreshConnection();
    return () => {
      effectCancelled = true;
      window.clearTimeout(timer);
    };
  }, [database, scope.businessId, scope.branchId, scope.stationId, scope.deviceId, scope.userId]);

  useEffect(() => {
    if (!cart?.customerId) {
      setSaleCustomer(null);
      return;
    }
    if (saleCustomer?.customerId === cart.customerId) return;
    let cancelled = false;
    void customers.get(cart.customerId).then((customer) => {
      if (!cancelled && customer) setSaleCustomer(customer);
    }).catch(() => undefined);
    return () => {
      cancelled = true;
    };
  }, [cart?.customerId, customers, saleCustomer?.customerId]);

  useEffect(() => {
    let cancelled = false;
    setCashStatus("loading");
    void cashSessions.current()
      .then((current) => {
        if (cancelled) return;
        setCashSession(current.session);
        setExpectedPreview(current.expectedCashPreview);
        setCashStatus("ready");
      })
      .catch(() => {
        if (cancelled) return;
        setCashStatus("unavailable");
      });
    return () => {
      cancelled = true;
    };
  }, [cashSessions]);

  const surfaceOpen = searchOpen || customerOpen || paying || opening || closing || closeBlocked || quantityTarget !== null || priceEdit !== null || missingItem !== null;

  function focusEntry() {
    if (surfaceOpen) return;
    const field = entryRef.current;
    if (!field || document.activeElement === field) return;
    field.focus({ preventScroll: true });
  }

  useEffect(() => {
    if (typeof window.matchMedia !== "function") return;
    const query = window.matchMedia("(pointer: fine)");
    const onChange = () => setFinePointer(query.matches);
    query.addEventListener("change", onChange);
    const keyboardQuery = window.matchMedia("(pointer: fine) and (hover: hover)");
    const onKeyboard = () => setDesktopKeyboard(keyboardQuery.matches);
    keyboardQuery.addEventListener("change", onKeyboard);
    return () => {
      query.removeEventListener("change", onChange);
      keyboardQuery.removeEventListener("change", onKeyboard);
    };
  }, []);

  useEffect(() => {
    if (customerOpen) customerInputRef.current?.focus();
  }, [customerOpen]);

  useEffect(() => {
    if ((!searchOpen && !customerOpen) || desktopKeyboard) {
      setSearchFrame(null);
      return;
    }
    const viewport = window.visualViewport;
    if (!viewport) return;
    const sync = () => {
      const keyboardOpen = window.innerHeight - viewport.height > 80;
      setSearchFrame(keyboardOpen ? { top: viewport.offsetTop + 12, height: Math.max(220, viewport.height - 24) } : null);
    };
    sync();
    viewport.addEventListener("resize", sync);
    viewport.addEventListener("scroll", sync);
    return () => {
      viewport.removeEventListener("resize", sync);
      viewport.removeEventListener("scroll", sync);
    };
  }, [searchOpen, customerOpen, desktopKeyboard]);

  useEffect(() => {
    entryValueRef.current = entry;
  }, [entry]);

  useEffect(() => {
    if (surfaceOpen) return;
    function routeScanner(event: KeyboardEvent) {
      if (event.ctrlKey || event.altKey || event.metaKey) return;
      const target = event.target;
      if (target instanceof HTMLElement && target.closest("input, textarea, select, [contenteditable='true']")) return;
      if (event.key === "Enter") {
        event.preventDefault();
        void submitEntry(entryValueRef.current);
        return;
      }
      if (event.key.length !== 1) return;
      event.preventDefault();
      const next = entryValueRef.current + event.key;
      entryValueRef.current = next;
      setEntry(next);
    }
    window.addEventListener("keydown", routeScanner);
    return () => window.removeEventListener("keydown", routeScanner);
  }, [surfaceOpen, cart]);

  useEffect(() => {
    if (surfaceOpen) return;
    suppressEntryFocus.current = false;
    entryRef.current?.focus({ preventScroll: true });
  }, [surfaceOpen]);

  useEffect(() => {
    function onKey(event: KeyboardEvent) {
      if (event.key !== "Escape") return;
      if (quantityTarget) setQuantityTarget(null);
      else if (priceEdit) setPriceEdit(null);
      else if (paying) setPaying(false);
      else if (opening) setOpening(false);
      else if (closeBlocked) setCloseBlocked(false);
      else if (closing) setClosing(false);
      else if (missingItem) setMissingItem(null);
      else if (customerOpen) setCustomerOpen(false);
      else if (searchOpen) closeSearch();
    }
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [quantityTarget, priceEdit, paying, opening, closing, closeBlocked, searchOpen, customerOpen, missingItem]);

  async function persist(next: CartDraft) {
    setCart(next);
    await saveDraft(database, next);
  }

  async function refreshConnection() {
    try {
      const response = await fetch("/health");
      setConnected(response.ok);
    } catch {
      setConnected(false);
    }
  }

  async function findExactItem(itemNumber: string) {
    const page = await items.loadItems({ search: itemNumber, activity: "active", page: 1, pageSize: 25, sort: "number" });
    return page.items.find((item) => item.itemNumber === itemNumber) ?? null;
  }

  async function addItem(item: ItemListItem) {
    if (!cart) return false;
    const quote = await pricing.resolveFixed(item.itemId, cart.customerId, item.itemNumber, item.itemName, "1");
    const added = addFixedLine(cart, quote);
    await persist(added);
    setConnected(true);
    setMessage("");
    return true;
  }

  async function addScale(value: string) {
    if (!cart) return false;
    const quote = await pricing.resolveVariable(value, cart.customerId, "");
    let named = quote;
    try {
      const page = await items.loadItems({ search: quote.referenceItemNumber, activity: "active", page: 1, pageSize: 25, sort: "number" });
      const reference = page.items.find((item) => item.itemNumber === quote.referenceItemNumber);
      named = { ...quote, referenceItemName: reference?.itemName || quote.referenceItemNumber };
    } catch {
      named = { ...quote, referenceItemName: quote.referenceItemNumber };
    }
    await persist(addVariableLine(cart, named));
    setConnected(true);
    setMessage("");
    return true;
  }

  async function submitEntry(text = entryValueRef.current) {
    if (lookupBusy.current || missingItem) return;
    const route = interpretFastEntry(text);
    if (route.kind === "empty") {
      focusEntry();
      return;
    }
    if (route.kind === "invalid") {
      setMessage(route.message);
      focusEntry();
      return;
    }

    lookupBusy.current = true;
    try {
      const added = route.kind === "scale"
        ? await addScale(route.value)
        : await addResolvedItem(route.itemNumber);
      if (added) {
        entryValueRef.current = "";
        setEntry("");
      }
      if (!suppressEntryFocus.current) focusEntry();
    } catch (error) {
      if (!(error instanceof PricingClientError)) setConnected(false);
      setMessage(error instanceof Error ? error.message : "تعذر إضافة السطر.");
      focusEntry();
    } finally {
      lookupBusy.current = false;
    }
  }

  function showMissingItem(itemNumber: string) {
    suppressEntryFocus.current = true;
    entryValueRef.current = "";
    setEntry("");
    setMissingItem(itemNumber);
  }

  function dismissMissingItem() {
    setMissingItem(null);
  }

  async function addResolvedItem(itemNumber: string) {
    const item = await findExactItem(itemNumber);
    if (!item) {
      showMissingItem(itemNumber);
      return false;
    }
    return addItem(item);
  }

  function closeSearch() {
    setSearchOpen(false);
    setSearch("");
    setSearchDone(false);
    setSearchSort(null);
    setSelectedResult(-1);
    setResults([]);
    setVisibleResults(POS_SEARCH_PAGE_SIZE);
    setSearchPage(1);
    setSearchTotal(0);
    focusEntry();
  }

  function placeSearchCaret(next: string, caret: number) {
    setSearch(next);
    requestAnimationFrame(() => {
      const input = searchInputRef.current;
      input?.focus({ preventScroll: true });
      input?.setSelectionRange(caret, caret);
    });
  }

  function writeSearch(value: string) {
    const input = searchInputRef.current;
    const start = input?.selectionStart ?? search.length;
    const end = input?.selectionEnd ?? search.length;
    placeSearchCaret(search.slice(0, start) + value + search.slice(end), start + value.length);
  }

  function backspaceSearch() {
    const input = searchInputRef.current;
    const start = input?.selectionStart ?? search.length;
    const end = input?.selectionEnd ?? search.length;
    if (start !== end) placeSearchCaret(search.slice(0, start) + search.slice(end), start);
    else if (start > 0) placeSearchCaret(search.slice(0, start - 1) + search.slice(end), start - 1);
    else searchInputRef.current?.focus({ preventScroll: true });
  }

  function toggleSearchSort(column: SearchSortColumn) {
    setSearchSort((current) => current?.column === column
      ? { column, direction: current.direction === "asc" ? "desc" : "asc" }
      : { column, direction: "asc" });
  }

  async function selectSearchItem(item: ItemListItem) {
    try {
      const added = await addItem(item);
      if (!added) return;
      closeSearch();
    } catch (error) {
      if (!(error instanceof PricingClientError)) setConnected(false);
      setMessage(error instanceof Error ? error.message : "تعذر إضافة السطر.");
    }
  }

  async function searchItems(page = 1) {
    setMessage("");
    try {
      const loaded = await items.loadItems({ search, activity: "active", page, pageSize: 25, sort: "number", resolvePrice: true, customerId: cart?.customerId ?? null });
      const next = page === 1 ? loaded.items : [...results, ...loaded.items];
      setResults(next);
      setSearchPage(page);
      setSearchTotal(loaded.totalCount);
      setVisibleResults(page === 1 ? POS_SEARCH_PAGE_SIZE : visibleResults + POS_SEARCH_PAGE_SIZE);
      if (page === 1) setSelectedResult(-1);
      setSearchDone(true);
      setConnected(true);
    } catch (error) {
      setConnected(false);
      setMessage(error instanceof Error ? error.message : "تعذر البحث.");
    }
  }

  function showMoreResults() {
    if (visibleResults < results.length) {
      setVisibleResults(visibleResults + POS_SEARCH_PAGE_SIZE);
      return;
    }
    if (results.length < searchTotal) void searchItems(searchPage + 1);
  }

  function openCustomerSearch() {
    setCustomerOpen(true);
    setCustomerError("");
    setCustomerMessage("");
  }

  async function searchCustomers(page = 1) {
    const term = customerQuery.trim();
    setCustomerError("");
    setCustomerMessage("");
    if (!term) {
      setCustomerResults([]);
      setCustomerDone(true);
      setCustomerSelected(-1);
      return;
    }
    setCustomerLoading(true);
    try {
      const loaded = await customers.search(term, page);
      setCustomerSyncState(loaded.syncState);
      setCustomerResults(page === 1 ? loaded.customers : [...customerResults, ...loaded.customers]);
      setCustomerTotal(loaded.totalCount);
      setCustomerPage(page);
      if (page === 1) setCustomerSelected(-1);
      setCustomerDone(true);
    } catch (error) {
      setCustomerError(error instanceof Error ? error.message : "تعذر تحميل بيانات الزبائن");
      setCustomerDone(false);
    } finally {
      setCustomerLoading(false);
    }
  }

  function chooseCustomer(customer: CustomerListItem) {
    setSaleCustomer(customer);
    setCustomerOpen(false);
    void changeCustomer(customer.customerId);
  }

  function useWalkInCustomer() {
    setSaleCustomer(null);
    setCustomerOpen(false);
    void changeCustomer(null);
  }

  async function submitOpen() {
    if (!isNonNegativeMoney(openingAmount)) {
      setCashMessage("الرصيد الافتتاحي يجب أن يكون صفراً أو أكبر، بدون قيمة سالبة.");
      return;
    }
    try {
      const opened = await cashSessions.open(openingAmount);
      setCashSession(opened);
      setCashStatus("ready");
      setOpening(false);
      setOpeningAmount("");
      setExpectedPreview(opened.openingCashAmount);
      setClosedSummary(null);
      setCashMessage("");
      focusEntry();
    } catch (error) {
      if (error instanceof CashSessionClientError && error.session) {
        setCashSession(error.session);
        setCashStatus("ready");
        setOpening(false);
        setCashMessage("");
        return;
      }
      if (error instanceof CashSessionClientError && error.unavailable) {
        setCashStatus("unavailable");
      }
      setCashMessage(error instanceof Error ? error.message : "تعذر بدء الوردية.");
    }
  }

  const showCheckoutKeypad = payMethod !== "Card";

  function pushPayDigit(digit: string) {
    const write = (current: string) => appendMoney(current, digit, payFresh);
    if (payField === "cashPart") setCashPart(write);
    else setTendered(write);
    setPayFresh(false);
  }

  function checkoutAmountsValid(total: string) {
    if (payMethod === "Card") return true;
    if (payMethod === "Cash") return isNonNegativeMoney(tendered) && compareDecimals(tendered, total) >= 0;
    return isPositiveDecimal(cashPart) && compareDecimals(cashPart, total) <= 0 && (tendered.trim() === "" || (isNonNegativeMoney(tendered) && compareDecimals(tendered, cashPart) >= 0));
  }

  async function saveHeldInvoice() {
    if (!cart || !claimSingleHold(holdBusy.current)) return false;
    const request = holdSaveRequest(cart);
    if ("error" in request) {
      holdBusy.current.busy = false;
      setMessage(request.error);
      return false;
    }
    try {
      await heldSales.save(request);
      await persist(cartAfterSuccessfulHold(scope));
      setMessage("تم حفظ الفاتورة كمسودة.");
      return true;
    } catch (error) {
      setMessage(error instanceof HeldSaleClientError ? error.message : "تعذر حفظ المسودة. الفاتورة الحالية ما زالت على الشاشة.");
      return false;
    } finally {
      holdBusy.current.busy = false;
    }
  }

  async function restoreHeld(saleId: string) {
    if (!cart || heldRestoring) return;
    setHeldRestoring(true);
    try {
      const detail = await heldSales.get(saleId);
      await persist(cartFromHeldSale(scope, detail));
      setHeldOpen(false);
      setHeldGuard(null);
      setMessage("");
    } catch (error) {
      setMessage(error instanceof HeldSaleClientError ? error.message : "تعذر استعادة المسودة.");
    } finally {
      setHeldRestoring(false);
    }
  }

  function openInvoicePreview() {
    if (!cart || cart.lines.length === 0) {
      setMessage("لا توجد أصناف للطباعة.");
      return;
    }
    setPrintSheet(buildA4Document({
      cart,
      businessName: user.businessDisplayName || user.businessName,
      branchName: user.branchName,
      cashierName: user.displayName,
      customerNumber: saleCustomer?.customerNumber ?? null,
      customerName: saleCustomer?.name ?? null,
    }));
  }

  function sharePreview() {
    const base = publicHttpsBase(import.meta.env.VITE_PUBLIC_INVOICE_BASE as string | undefined);
    if (!printSheet || printSheet.draft || !completedShare || !base) return;
    window.open(whatsAppShareUrl(completedShare.phone, `${base}/invoices/${completedShare.saleId}`), "_blank", "noopener");
  }

  function requestRestore(saleId: string) {
    if (!cart) return;
    if (currentInvoiceNeedsGuard(cart)) {
      setHeldGuard(saleId);
      return;
    }
    void restoreHeld(saleId);
  }

  async function submitCheckout() {
    if (!cart || cart.lines.length === 0 || checkoutPending) return;
    const total = cartTotals(cart).total;
    if (!checkoutAmountsValid(total)) {
      setMessage(payMethod === "Split" ? "راجع المبلغ النقدي والمبلغ المستلم." : "المبلغ المستلم أقل من الإجمالي.");
      return;
    }
    const payments = payMethod === "Card"
      ? [{ method: "Card", amount: total, tenderedAmount: null, clientPaymentKey: crypto.randomUUID() }]
      : payMethod === "Cash"
        ? [{ method: "Cash", amount: total, tenderedAmount: tendered, clientPaymentKey: crypto.randomUUID() }]
        : [
            { method: "Cash", amount: cashPart, tenderedAmount: tendered || cashPart, clientPaymentKey: crypto.randomUUID() },
            { method: "Card", amount: subtractDecimals(total, cashPart), tenderedAmount: null, clientPaymentKey: crypto.randomUUID() },
          ];
    setCheckoutPending(true);
    try {
      const heldId = cart.heldSaleId;
      const phone = saleCustomer?.phone ?? null;
      const completed = await checkoutClient.checkout(cart, payments);
      if (heldId) {
        try { await heldSales.release(heldId); } catch { setMessage("تم البيع، وتعذر إغلاق المسودة المحفوظة."); }
      }
      const next = createCart(scope);
      await persist(next);
      setSaleCustomer(null);
      setCompletedShare({ saleId: completed.saleId, phone });
      setReceipt(completed);
      setPaying(false);
      setTendered("");
      setCashPart("");
      setMessage("");
      focusEntry();
    } catch (error) {
      setMessage(error instanceof CheckoutClientError ? error.message : "تعذر إتمام البيع. السلة محفوظة.");
    } finally {
      setCheckoutPending(false);
    }
  }

  async function submitClose() {
    if (!isNonNegativeMoney(countedAmount)) {
      setCashMessage("المبلغ الفعلي يجب أن يكون صفراً أو أكبر.");
      return;
    }
    try {
      const closed = await cashSessions.close(countedAmount);
      setClosedSummary(closed);
      setCashSession(null);
      setExpectedPreview(null);
      setCashStatus("ready");
      setClosing(false);
      setCountedAmount("");
      setCashMessage("");
      focusEntry();
    } catch (error) {
      if (error instanceof CashSessionClientError && error.unavailable) {
        setCashStatus("unavailable");
      }
      setCashMessage(error instanceof Error ? error.message : "تعذر إغلاق الوردية.");
    }
  }

  function openPrice(lineId: string, kind: "unit" | "total", value: string) {
    setPriceEdit({ lineId, kind, value });
    setPriceError("");
  }

  function confirmPrice(value: string) {
    if (!cart || !priceEdit || !/^\d+(\.\d{1,2})?$/.test(value)) {
      setPriceError("أدخل سعراً صحيحاً أكبر من أو يساوي صفر");
      return;
    }
    const next = priceEdit.kind === "unit"
      ? overrideUnitPrice(cart, priceEdit.lineId, value)
      : overrideLineTotal(cart, priceEdit.lineId, value);
    void persist(next);
    setPriceEdit(null);
  }

  function openQuantity(lineId: string, quantity: string) {
    setQuantityTarget(lineId);
    setQuantityDraft(quantity);
    setQuantityError("");
  }

  function confirmQuantity(value: string) {
    if (!cart || !quantityTarget || !isPositiveInteger(value)) {
      setQuantityError("أدخل كمية صحيحة أكبر من صفر");
      return;
    }
    const target = cart.lines.find((line) => line.cartLineId === quantityTarget);
    if (!target) return;
    const next = target.lineType === "Fixed"
      ? updateFixedQuantity(cart, quantityTarget, value)
      : updateVariableQuantity(cart, quantityTarget, value);
    void commitRepriced(next);
    setQuantityTarget(null);
  }

  function pricingGateway(current: CartDraft): PricingGateway {
    return {
      resolveFixed: (itemId, nextCustomer, quantity) => {
        const line = current.lines.find((item): item is FixedCartLine => item.lineType === "Fixed" && item.itemId === itemId);
        return pricing.resolveFixed(itemId, nextCustomer, line?.itemNumber ?? "", line?.itemName ?? "", quantity ?? line?.quantity ?? "1");
      },
      resolveVariable: (entered, nextCustomer) => {
        const line = current.lines.find((item): item is VariableCartLine => item.lineType === "VariableValueByWeight" && item.enteredScaleValue === entered);
        return pricing.resolveVariable(entered, nextCustomer, line?.referenceItemName ?? "");
      },
    };
  }

  async function commitRepriced(next: CartDraft) {
    const repriced = await repriceCart(next, next.customerId, pricingGateway(next));
    await persist(repriced.status === "NeedsReprice" ? next : repriced);
    setMessage(repriced.status === "NeedsReprice" ? "تعذر إعادة تسعير بعض الأسطر. السلة محفوظة." : "");
  }

  async function changeCustomer(customerId: string | null) {
    if (!cart) return;
    const next = await repriceCart(cart, customerId, pricingGateway(cart));
    await persist(next);
    setMessage(next.status === "NeedsReprice" ? "تعذر إعادة تسعير بعض الأسطر. السلة محفوظة." : "");
  }

  const renderMark = useRef("");
  useEffect(() => {
    if (!import.meta.env.DEV) return;
    const phase = cartRestore === "loading" || !cart ? "loading" : cartRestore;
    const event = `render-${phase}`;
    if (renderMark.current !== event) {
      renderMark.current = event;
      recordCartStorageEvent(event);
    }
    publishCartRestoreView({
      effectRunId: restoreRun.current,
      cartRestore,
      cart: cart ? "present" : "null",
      scopeKey: scope.businessId && scope.deviceId && scope.userId ? "present" : "missing",
    });
  }, [cart, cartRestore, scope.businessId, scope.deviceId, scope.userId]);

  if (cartRestore === "loading" || !cart) return <section className="card">جاري استعادة السلة…</section>;
  const totals = cartTotals(cart);
  const orderedResults = searchSort ? sortSearchResults(results, searchSort.column, searchSort.direction, (item) => item.resolvedPrice ?? null) : results;
  const shownResults = orderedResults.slice(0, visibleResults);
  const orderedCustomers = customerSort ? sortCustomers(customerResults, customerSort.column, customerSort.direction) : customerResults;

  function onWorkspacePointerDown(event: PointerEvent) {
    gesture.current = { x: event.clientX, y: event.clientY, moved: false, scrolled: false, cancelled: false };
  }

  function onWorkspacePointerMove(event: PointerEvent) {
    if (Math.abs(event.clientX - gesture.current.x) > 10 || Math.abs(event.clientY - gesture.current.y) > 10) {
      gesture.current.moved = true;
    }
  }

  function onWorkspacePointerCancel() {
    gesture.current.cancelled = true;
    gesture.current.moved = true;
  }

  function onCartScroll() {
    gesture.current.scrolled = true;
  }

  function onWorkspacePointerUp(event: PointerEvent) {
    if (event.pointerType === "touch" || event.pointerType === "pen") return;
    const pan = gesture.current.moved || gesture.current.scrolled || gesture.current.cancelled;
    if (pan || surfaceOpen) return;
    const target = event.target;
    if (!(target instanceof Element)) return;
    if (target.closest("button, input, textarea, select, a, [role='button'], [contenteditable='true'], [data-pos-interactive='true']")) return;
    requestAnimationFrame(() => focusEntry());
  }

  function pressPad(token: string) {
    const entryInput = entryRef.current;
    const active = document.activeElement;
    const target = active instanceof HTMLInputElement ? active : entryInput;
    if (!target) return;
    if (target === entryInput) {
      if (token === "Enter") { void submitEntry(); return; }
      if (token === "BS") { setEntry((value) => value.slice(0, -1)); return; }
      if (token === "Del") { setEntry(""); return; }
      if (/^[0-9]$/.test(token)) setEntry((value) => `${value}${token}`);
      return;
    }
    const key = token === "BS" ? "Backspace" : token === "Del" ? "Delete" : token;
    if (target.readOnly || token === "Enter" || token === "BS" || token === "Del") {
      target.dispatchEvent(new KeyboardEvent("keydown", { key, bubbles: true, cancelable: true }));
      return;
    }
    const start = target.selectionStart ?? target.value.length;
    const end = target.selectionEnd ?? start;
    const next = `${target.value.slice(0, start)}${token}${target.value.slice(end)}`;
    Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, "value")?.set?.call(target, next);
    target.dispatchEvent(new Event("input", { bubbles: true }));
  }

  const priceTier = [...new Set(cart.lines.flatMap((line) => line.lineType === "Fixed" ? [line.priceTypeCode] : []))].join("، ");

  return (
    <section className="card pos-screen pos-workspace" onPointerDown={onWorkspacePointerDown} onPointerMove={onWorkspacePointerMove} onPointerUp={onWorkspacePointerUp} onPointerCancel={onWorkspacePointerCancel}>
      <div className="pos-desktop-shell">
      <div className="pos-main-workspace">
      <div className="pos-top">
      {(() => {
        const titleRow = (
          <div className="card-header pos-title-row">
            <button className="nav-button" type="button" onClick={onBack}><ArrowLeft className="pos-pro-icon" aria-hidden="true" />رجوع</button>
            <h2>نقطة البيع</h2>
          </div>
        );
        const slot = professionalShell ? document.getElementById("pos-topbar-slot") : null;
        return slot ? createPortal(titleRow, slot) : titleRow;
      })()}
      <div className="pos-toolbar">
        <form className="pos-fast" onSubmit={(event) => { event.preventDefault(); void submitEntry(); }}>
          <label htmlFor="pos-fast-entry">رقم الصنف</label>
          <input
            id="pos-fast-entry"
            ref={entryRef}
            autoComplete="off"
            aria-autocomplete="none"
            aria-label="رقم الصنف"
            aria-describedby="pos-fast-help"
            value={entry}
            onChange={(event) => setEntry(event.target.value)}
            placeholder="أدخل رقم الصنف أو امسح الباركود..."
          />
          <button className="nav-button" type="submit"><CirclePlus className="pos-pro-icon" aria-hidden="true" />إضافة</button>
        </form>
        <button className="nav-button" type="button" onClick={() => setSearchOpen((open) => !open)}><Search className="pos-pro-icon" aria-hidden="true" />بحث عن صنف</button>
        <p className="pos-meta">الزبون: {cart.customerId ? cart.customerId : "زبون عادي"} · <span className={connected ? "status-ok" : "status-error"}>{connected ? "متصل بخادم الفرع" : "خادم الفرع غير متاح. السلة محفوظة."}</span></p>
      </div>
      <p id="pos-fast-help" className="pos-help">من 1 إلى 300 تُحسب قيمة ميزان. لإضافة صنف برقمه استخدم # مثل #224.</p>
      </div>
      {cart.status === "NeedsReprice" ? <p className="status-error">السلة تحتاج إعادة تسعير.</p> : null}
      <div className="pos-info pos-pro" aria-label="بيانات الزبون والتسعير">
        <div className="pos-original-total"><span><Coins className="pos-pro-icon" aria-hidden="true" />المجموع قبل الخصم</span><strong>{formatPosMoney(originalCartTotal(cart))}</strong></div>
        <div><button type="button" onClick={openCustomerSearch}><span><UserRound className="pos-pro-icon" aria-hidden="true" />رقم الزبون</span><strong title={saleCustomer?.customerNumber ?? ""}>{saleCustomer ? (saleCustomer.customerNumber ?? "—") : "—"}</strong></button></div>
        <div><button type="button" onClick={openCustomerSearch}><span>اسم الزبون</span><strong title={saleCustomer?.name ?? "زبون عادي"}>{saleCustomer?.name ?? (cart.customerId ? "—" : "زبون عادي")}</strong></button></div>
        {saleCustomer ? <div><button type="button" onClick={() => setPasskeyVerify(true)}>التحقق من هوية الزبون</button></div> : null}
        <div><span>نوع الزبون</span><strong>{saleCustomer ? (saleCustomer.classificationName || "—") : cart.customerId ? "—" : "عادي"}</strong></div>
        <div><span><Tag className="pos-pro-icon" aria-hidden="true" />التسعيرة</span><strong dir="ltr">{saleCustomer?.priceCode || (cart.customerId ? (priceTier || "—") : "Price0")}</strong></div>
        <div><span><Store className="pos-pro-icon" aria-hidden="true" />النقطة</span><strong title={session.stationName}>{session.stationName || "—"}</strong></div>
        <div><span><UserRound className="pos-pro-icon" aria-hidden="true" />اسم الموظف</span><strong title={user.displayName}>{user.displayName}</strong></div>
        <div><span><Percent className="pos-pro-icon" aria-hidden="true" />الخصم</span><strong>{saleCustomer ? saleCustomer.variableValueDiscountPercent : "0"}</strong></div>
      </div>
      <div className="pos-summary">
        <div>
          <div className="pos-total-label"><Coins className="pos-pro-icon" aria-hidden="true" />المجموع</div>
          <strong className="pos-total">{totals.total} ₪</strong>
        </div>
        <span><ShoppingBag className="pos-pro-icon" aria-hidden="true" />{totals.lineCount} أصناف</span>
        <span><Box className="pos-pro-icon" aria-hidden="true" />{addDecimals(totals.fixedQuantity, cart.lines.reduce((sum, line) => line.lineType === "VariableValueByWeight" ? addDecimals(sum, line.quantity) : sum, "0"))} وحدات</span>
        <span><UserRound className="pos-pro-icon" aria-hidden="true" />{saleCustomer?.name ?? "زبون عادي"}</span>
        <span className={cashSession ? "status-ok" : "status-error"}>
          {cashStatus === "loading" ? "جاري قراءة الوردية" : cashStatus === "unavailable" ? "حالة الوردية غير متاحة" : cashSession ? "● الوردية مفتوحة" : "● الوردية مغلقة"}
        </span>
        {cashSession ? <span className="pos-shift-who">الموظف: {cashSession.openedByDisplayName} · بدأت: {shiftClock(cashSession.openedAt)}</span> : null}
        {cashSession ? <button className="nav-button" type="button" onClick={() => cart.lines.length > 0 ? setCloseBlocked(true) : setClosing(true)}><LockKeyhole className="pos-pro-icon" aria-hidden="true" />إغلاق الوردية</button> : null}
      </div>
      <div className="pos-cart pos-cart-region" role="table" onScroll={onCartScroll}>
        <div className="pos-cart-head-bar">
          <div className="pos-cart-head" role="row">
            <span className="pos-col-item" role="columnheader">الصنف</span>
            <span className="pos-col-type" role="columnheader">النوع</span>
            <span className="pos-col-qty" role="columnheader">الكمية</span>
            <span className="pos-col-base" role="columnheader">السعر الأصلي</span>
            <span className="pos-col-price" role="columnheader">سعر الوحدة</span>
            <span className="pos-col-total" role="columnheader">الإجمالي</span>
            <span className="pos-col-action" role="columnheader">إجراء</span>
          </div>
        </div>
        <div className="pos-cart-rows" ref={cartRegionRef} onScroll={onCartScroll}>
        {cart.lines.length === 0 ? <p className="pos-empty pos-pro">لا توجد أصناف في السلة<small>أدخل رقم الصنف أو امسح الباركود للبدء</small></p> : null}
        {cart.lines.map((line) => (
          <div key={line.cartLineId} className={cart.failedLineIds.includes(line.cartLineId) ? "pos-line failed" : "pos-line"} role="row">
            {line.lineType === "Fixed" ? (
              <>
                <div className="pos-item-name pos-col-item" role="cell"><strong className="pos-item-title">{line.itemName}</strong><span className="pos-item-number">{line.itemNumber}</span><span className="pos-inline-type">عادي</span>{line.priceSource === "CustomerItemSpecial" && line.priceOverrideMode === "None" ? <small>سعر استثنائي</small> : null}</div>
                <div className="pos-col-type" role="cell">عادي</div>
                <div className="pos-col-qty" role="cell"><QuantityStepper
                  label={line.itemName}
                  quantity={line.quantity}
                  onOpen={() => openQuantity(line.cartLineId, line.quantity)}
                  onStep={(direction) => {
                    const next = direction === "up" ? addDecimals(line.quantity, "1") : subtractDecimals(line.quantity, "1");
                    if (isPositiveInteger(next)) void commitRepriced(updateFixedQuantity(cart, line.cartLineId, next)).then(() => focusEntry());
                  }}
                /></div>
                <div className="pos-col-base" role="cell">{line.originalUnitPrice == null ? "—" : formatPosMoney(line.originalUnitPrice)}</div>
                <div className="pos-col-price" role="cell"><PriceBox value={formatPosMoney(line.unitPrice)} manual={line.priceOverrideMode !== "None"} onClick={() => openPrice(line.cartLineId, "unit", formatPosMoney(line.unitPrice))} /></div>
                <div className="pos-col-total" role="cell"><PriceBox value={formatPosMoney(line.lineTotal)} manual={line.priceOverrideMode !== "None"} onClick={() => openPrice(line.cartLineId, "total", formatPosMoney(line.lineTotal))} /></div>
              </>
            ) : (
              <>
                <div className="pos-item-name pos-col-item" role="cell">
                  <strong className="pos-item-title">{line.referenceItemName}</strong>
                  <span className="pos-item-number">{line.referenceItemNumber}</span>
                  <span className="pos-inline-type">وزن</span>
                  <p className="pos-weight">{roundDecimal(line.weightGrams, 2)} غ للوحدة</p>
                  {line.quantity !== "1" ? <p className="pos-weight">إجمالي الوزن: {roundDecimal(multiplyDecimals(line.weightGrams, line.quantity), 2)} غ</p> : null}
                </div>
                <div className="pos-col-type" role="cell">وزن</div>
                <div className="pos-col-qty" role="cell"><QuantityStepper
                  label={line.referenceItemName}
                  quantity={line.quantity}
                  onOpen={() => openQuantity(line.cartLineId, line.quantity)}
                  onStep={(direction) => {
                    const next = direction === "up" ? addDecimals(line.quantity, "1") : subtractDecimals(line.quantity, "1");
                    if (isPositiveInteger(next)) void persist(updateVariableQuantity(cart, line.cartLineId, next)).then(() => focusEntry());
                  }}
                /></div>
                <div className="pos-col-base" role="cell" title="قيمة التوزين الأصلية">{line.enteredScaleValue == null ? "—" : formatPosMoney(line.enteredScaleValue)}</div>
                <div className="pos-col-price" role="cell"><PriceBox value={formatPosMoney(line.unitPrice)} manual={line.priceOverrideMode !== "None"} onClick={() => openPrice(line.cartLineId, "unit", formatPosMoney(line.unitPrice))} /></div>
                <div className="pos-col-total" role="cell"><PriceBox value={formatPosMoney(line.lineTotal)} manual={line.priceOverrideMode !== "None"} onClick={() => openPrice(line.cartLineId, "total", formatPosMoney(line.lineTotal))} /></div>
              </>
            )}
            <div className="pos-col-action" role="cell"><button type="button" onClick={() => void persist(removeLine(cart, line.cartLineId)).then(() => focusEntry())}>حذف</button></div>
          </div>
        ))}
        </div>
      </div>
      {receipt ? <p className="status-ok">تمت عملية البيع بنجاح. رقم الفاتورة {receipt.saleNumber}. المجموع {receipt.total} ₪.{receipt.payments.some((payment) => Number(payment.changeAmount) > 0) ? ` الباقي ${receipt.payments.find((payment) => payment.method === "Cash")?.changeAmount ?? ""} ₪.` : ""}</p> : null}
      {message && !searchOpen ? <p className="status-error">{message}</p> : null}
      <div className="actions pos-action-bar">
        <button className="nav-button" type="button" onClick={() => void changeCustomer(cart.customerId)}><RefreshCw className="pos-pro-icon" aria-hidden="true" />تحديث الأسعار</button>
        {confirmClear ? (
          <button className="danger-button" type="button" onClick={() => { setConfirmClear(false); void persist(clearCart(cart)); }}>تأكيد مسح السلة</button>
        ) : (
          <button className="nav-button" type="button" onClick={() => setConfirmClear(true)}><Trash2 className="pos-pro-icon" aria-hidden="true" />مسح السلة</button>
        )}
        {cashStatus === "ready" && !cashSession ? (
          <button className="primary-button" type="button" onClick={() => { setCashMessage(""); setOpening(true); }}>بدء الوردية</button>
        ) : (
          <button className="primary-button" type="button" disabled={cart.lines.length === 0 || !cashSession || checkoutPending} onClick={() => { setPaying(true); setReceipt(null); }}><ShoppingCart className="pos-pro-icon" aria-hidden="true" />إتمام البيع</button>
        )}
      </div>
      </div>
      <aside className="pos-sidebar pos-pro" aria-label="الشريط الجانبي">
        <section className="pos-side-card">
          <h3>إجراءات سريعة</h3>
          <div className="pos-quick-grid">
            <button type="button" title="حفظ الطلب الحالي" onClick={() => void saveHeldInvoice()}><Save aria-hidden="true" /><span>حفظ</span></button>
            <button type="button" title="طباعة ومشاركة" onClick={openInvoicePreview}><Printer aria-hidden="true" /><span>طباعة ومشاركة</span></button>
            <button type="button" title="المحفوظات" onClick={() => setHeldOpen(true)}><Archive aria-hidden="true" /><span>المحفوظات</span></button>
            {QUICK_ACTIONS.map(({ label, icon: Icon, title }) => {
              const creditAction = label === "تأجيل الدفع";
              const creditAllowed = creditEnabled && (user.permissions.includes("Credit.Create") || user.permissions.includes("System.Administer"));
              return (
                <button key={label} type="button" disabled={!creditAction || !creditAllowed} title={title} onClick={() => {
                  if (!creditAction || !creditAllowed) return;
                  if (!creditOperationId) setCreditOperationId(crypto.randomUUID());
                  setCreditOpen(true);
                }}><Icon aria-hidden="true" /><span>{label}</span></button>
              );
            })}
            <button type="button" onClick={onLogout} disabled={!onLogout} title="تسجيل الخروج"><LogOut aria-hidden="true" /><span>تسجيل الخروج</span></button>
            <button type="button" disabled title="الجرد"><PackageSearch aria-hidden="true" /><span>الجرد</span></button>
          </div>
        </section>
        <section className="pos-side-card pos-pad-card">
          <h3><Keyboard aria-hidden="true" />لوحة الأرقام</h3>
          <div className="pos-side-pad" dir="ltr">
            {["Del", "/", "*", "BS", "7", "8", "9", "-", "4", "5", "6", "+", "1", "2", "3", "Enter", "0", "."].map((key) => (
              <button
                key={key}
                type="button"
                className={key === "Enter" ? "is-enter" : key === "0" ? "is-wide" : undefined}
                onMouseDown={(event) => event.preventDefault()}
                aria-label={key === "Enter" ? "إدخال" : `مفتاح ${key}`}
                onClick={() => pressPad(key)}
              >{key === "Enter" ? "إدخال" : key}</button>
            ))}
          </div>
        </section>
      </aside>
      </div>
      {passkeyVerify && saleCustomer ? <PasskeyQrPanel session={session} customerId={saleCustomer.customerId} purpose="verification" canReplace={false} onClose={() => setPasskeyVerify(false)} /> : null}
      {printSheet ? (
        <InvoicePreview
          document={printSheet}
          shareUrl={publicHttpsBase(import.meta.env.VITE_PUBLIC_INVOICE_BASE as string | undefined) && completedShare ? `${publicHttpsBase(import.meta.env.VITE_PUBLIC_INVOICE_BASE as string | undefined)}/invoices/${completedShare.saleId}` : null}
          branchName={user.branchName}
          stationName={session.stationName}
          userName={user.displayName}
          onPrint={() => window.print()}
          onWhatsApp={sharePreview}
          onBack={() => setPrintSheet(null)}
        />
      ) : null}
      {heldOpen ? (
        <HeldDraftsScreen
          session={session}
          desktopKeyboard={desktopKeyboard}
          restoring={heldRestoring}
          guardMessage={heldGuard ? "الفاتورة الحالية غير محفوظة. ماذا تريد قبل استعادة المسودة؟" : null}
          onClose={() => { setHeldOpen(false); setHeldGuard(null); }}
          onRestore={requestRestore}
          onSaveThenRestore={() => { if (heldGuard) void saveHeldInvoice().then((saved) => { if (saved) void restoreHeld(heldGuard); }); }}
          onDiscardThenRestore={() => { if (heldGuard) void restoreHeld(heldGuard); }}
          onCancelGuard={() => setHeldGuard(null)}
        />
      ) : null}
      {missingItem ? (
        <div className="pos-overlay">
          <form className="pos-modal pos-missing-item" role="dialog" aria-label="تنبيه" onSubmit={(event) => { event.preventDefault(); dismissMissingItem(); }}>
            <h3><CircleAlert aria-hidden="true" />تنبيه</h3>
            <p>الصنف رقم {missingItem} غير موجود</p>
            <button className="primary-button" type="submit" autoFocus>موافق</button>
          </form>
        </div>
      ) : null}
      {debtCustomer && !debtRepay ? (
        <DebtAccountScreen session={session} customer={debtCustomer} canCollect={collectionEnabled && (user.permissions.includes("Credit.Collect") || user.permissions.includes("System.Administer"))} onBack={() => setDebtCustomer(null)} onRepay={() => setDebtRepay(true)} />
      ) : null}
      {debtCustomer && debtRepay ? (
        <DebtRepaymentScreen session={session} customer={debtCustomer} branchName={session.branchName} cashierName={user.displayName} cashSessionLabel={cashSession?.status === "Open" ? "مفتوحة" : "غير مفتوحة"} canReverse={reversalEnabled && (user.permissions.includes("Credit.Reverse") || user.permissions.includes("System.Administer"))} onBack={() => setDebtRepay(false)} />
      ) : null}
      {creditOpen && cart && creditOperationId ? (
        <CreditSaleScreen
          session={session}
          cart={cart}
          customerName={saleCustomer?.name ?? "زبون عادي"}
          branchName={session.branchName}
          userName={user.displayName}
          canCreate={user.permissions.includes("Credit.Create") || user.permissions.includes("System.Administer")}
          operationId={creditOperationId}
          onBack={() => setCreditOpen(false)}
          onCompleted={() => { void persist(clearCart(cart)); setSaleCustomer(null); setCreditOperationId(""); setCreditOpen(false); }}
        />
      ) : null}
      {registerOpen ? (
        <NewCustomerScreen
          session={session}
          customerId={editCustomerId}
          permissions={user.permissions}
          title={editCustomerId ? "تعديل زبون" : "إضافة زبون جديد"}
          onBack={() => { setRegisterOpen(false); setEditCustomerId(undefined); }}
          onSaved={(customerId) => { void customers.get(customerId).then((customer) => { if (customer) chooseCustomer(customer); }); setRegisterOpen(false); setEditCustomerId(undefined); }}
        />
      ) : null}
      {customerOpen ? (
        <section className={desktopKeyboard ? "new-customer full-keyboard-screen has-keyboard" : "new-customer"} dir="rtl" aria-label="بحث عن زبون">
          <header className="new-customer-bar">
            <strong className="new-customer-brand">XtraPOS</strong>
            <h2>بحث عن زبون</h2>
            <button type="button" className="new-customer-back" onClick={() => setCustomerOpen(false)}><ArrowLeft aria-hidden="true" />رجوع</button>
          </header>
          <form className="customer-search-content" onSubmit={(event) => { event.preventDefault(); void searchCustomers(1); }}>
            <div className="pos-search-row">
              <input ref={customerInputRef} autoFocus enterKeyHint="search" aria-label="بحث عن زبون" value={customerQuery} placeholder="اسم الزبون أو رقمه أو الهاتف..." onChange={(event) => { setCustomerQuery(event.target.value); setCustomerDone(false); setCustomerSort(null); }} onKeyDown={(event) => {
                if (event.key === "Enter") { event.preventDefault(); void searchCustomers(1); }
                if (event.key === "ArrowDown") { event.preventDefault(); setCustomerSelected((index) => Math.min(orderedCustomers.length - 1, Math.max(-1, index) + 1)); }
                if (event.key === "ArrowUp") { event.preventDefault(); setCustomerSelected((index) => index <= 0 ? 0 : index - 1); }
              }} />
              <button className="nav-button" type="submit">عرض النتائج</button>
            </div>
            {customerSyncLabel(customerSyncState) ? <p className="pos-search-count" aria-live="polite">{customerSyncLabel(customerSyncState)}</p> : null}
            {customerDone ? <p className="pos-search-count">{customerTotal} نتيجة</p> : null}
            {customerError ? <p className="status-error">{customerError}</p> : null}
            {customerMessage ? <p className="pos-search-count">{customerMessage}</p> : null}
            <div className="pos-search-table customer-search-grid" role="table" aria-label="نتائج الزبائن">
              <div className="pos-search-columns customer-search-header" role="row">
                {([
                  ["number", "رقم الزبون"],
                  ["name", "اسم الزبون"],
                  ["phone", "الهاتف"],
                  ["type", "نوع الزبون"],
                  ["price", "التسعيرة"],
                ] as const).map(([column, label]) => (
                  <button key={column} type="button" className="customer-search-column-header" role="columnheader" aria-sort={customerSort?.column === column ? (customerSort.direction === "asc" ? "ascending" : "descending") : "none"} onClick={() => setCustomerSort((current) => current?.column === column ? { column, direction: current.direction === "asc" ? "desc" : "asc" } : { column, direction: "asc" })}>
                    {label}
                    {customerSort?.column === column ? (customerSort.direction === "asc" ? <ArrowUp aria-hidden="true" /> : <ArrowDown aria-hidden="true" />) : <ArrowUpDown aria-hidden="true" />}
                  </button>
                ))}
              </div>
              <div className="pos-search-body">
                {customerLoading ? <p className="pos-search-empty">جاري البحث…</p> : null}
                {orderedCustomers.map((customer, index) => (
                  <button key={customer.customerId} type="button" className={index === customerSelected ? "customer-search-row is-selected" : "customer-search-row"} onClick={() => chooseCustomer(customer)}>
                    <span className="customer-search-cell" role="cell">{customer.customerNumber ?? "—"}</span>
                    <span className="customer-search-cell" role="cell" title={customer.name}>{customer.name}</span>
                    <span className="customer-search-cell" role="cell" dir="ltr">{customer.phone ?? "—"}</span>
                    <span className="customer-search-cell" role="cell">{customer.classificationName ?? "—"}</span>
                    <span className="customer-search-cell" role="cell" dir="ltr">{customer.priceCode ?? "—"}</span>
                  </button>
                ))}
                {customerDone && !customerError && !customerLoading && orderedCustomers.length === 0 ? <p className="pos-search-empty">لا يوجد زبائن مطابقون للبحث</p> : null}
              </div>
            </div>
            {customerResults.length < customerTotal ? <button className="nav-button" type="button" onClick={() => void searchCustomers(customerPage + 1)}>المزيد</button> : null}
            <div className="pos-customer-actions">
              <button className="nav-button" type="button" onClick={() => { setCustomerOpen(false); setEditCustomerId(undefined); setRegisterOpen(true); }}>زبون جديد</button>
              <button className="nav-button" type="button" disabled={!orderedCustomers[customerSelected]} onClick={() => { const customer = orderedCustomers[customerSelected]; if (!customer) return; setCustomerOpen(false); setEditCustomerId(customer.customerId); setRegisterOpen(true); }}>تعديل الزبون</button>
              <button className="nav-button" type="button" disabled={!orderedCustomers[customerSelected] || !(user.permissions.includes("Credit.View") || user.permissions.includes("System.Administer"))} onClick={() => { const customer = orderedCustomers[customerSelected]; if (!customer) return; setDebtCustomer({ customerId: customer.customerId, customerNumber: customer.customerNumber, name: customer.name, phone: customer.phone }); setDebtRepay(false); setCustomerOpen(false); }}>حساب ديون الزبون</button>
              <button className="nav-button" type="button" onClick={useWalkInCustomer}>استخدام زبون عادي</button>
              <button className="primary-button" type="button" disabled={customerSelected < 0 || !orderedCustomers[customerSelected]} onClick={() => { const customer = orderedCustomers[customerSelected]; if (customer) chooseCustomer(customer); }}>إدخال</button>
            </div>
          </form>
          {desktopKeyboard ? <FullKeyboard onEnter={() => void searchCustomers(1)} /> : null}
        </section>
      ) : null}
      {searchOpen ? (
        <div className="pos-overlay">
          <div
            className={desktopKeyboard ? "pos-modal pos-search-dialog pos-search-desktop" : "pos-modal pos-search-dialog pos-search-touch"}
            role="dialog"
            aria-label="نافذة البحث"
            style={searchFrame ? { top: searchFrame.top, height: searchFrame.height, maxHeight: searchFrame.height } : undefined}
          >
          <form className="pos-search" onSubmit={(event) => { event.preventDefault(); void searchItems(1); }}>
            <header className="pos-search-headbar">
              <h3><Search aria-hidden="true" />بحث عن صنف</h3>
              <button type="button" className="pos-search-close" aria-label="إغلاق" onClick={closeSearch}><X aria-hidden="true" /></button>
            </header>
            <div className="pos-search-row">
              <input ref={searchInputRef} autoFocus enterKeyHint="search" aria-label="بحث عن صنف" value={search} placeholder="اسم الصنف أو رقم الصنف..." onChange={(event) => { setSearch(event.target.value); setSearchDone(false); setSearchSort(null); }} onKeyDown={(event) => {
                if (event.key === "ArrowDown") { event.preventDefault(); setSelectedResult((index) => Math.min(shownResults.length - 1, Math.max(-1, index) + 1)); }
                if (event.key === "ArrowUp") { event.preventDefault(); setSelectedResult((index) => index <= 0 ? 0 : index - 1); }
              }} />
              <button className="nav-button" type="submit">عرض النتائج</button>
            </div>
            {searchDone ? <p className="pos-search-count">{searchTotal} نتيجة</p> : null}
            {message ? <p className="status-error">{message}</p> : null}
            <div className="pos-search-table" role="table" aria-label="نتائج البحث">
              <div className="pos-search-columns" role="row">
                {([
                  ["number", "رقم الصنف"],
                  ["name", "اسم الصنف"],
                  ["price", "السعر"],
                ] as const).map(([column, label]) => (
                  <button key={column} type="button" role="columnheader" aria-sort={searchSort?.column === column ? (searchSort.direction === "asc" ? "ascending" : "descending") : "none"} onClick={() => toggleSearchSort(column)}>
                    {label}
                    {searchSort?.column === column
                      ? (searchSort.direction === "asc" ? <ArrowUp aria-hidden="true" /> : <ArrowDown aria-hidden="true" />)
                      : <ArrowUpDown aria-hidden="true" />}
                  </button>
                ))}
              </div>
              <div className="pos-search-body">
                {shownResults.map((item, index) => (
                  <button key={item.itemId} type="button" className={index === selectedResult ? "is-selected" : undefined} onClick={() => void selectSearchItem(item)}>
                    <span role="cell">{item.itemNumber}</span>
                    <span role="cell">{item.itemName}</span>
                    <span role="cell">{item.resolvedPrice === null || item.resolvedPrice === undefined ? "—" : formatPosMoney(String(item.resolvedPrice))}</span>
                  </button>
                ))}
                {searchDone && !message && shownResults.length === 0 ? <p className="pos-search-empty">لا توجد أصناف مطابقة للبحث</p> : null}
              </div>
            </div>
            {visibleResults < results.length || results.length < searchTotal ? (
              <button className="nav-button" type="button" onClick={showMoreResults}>المزيد</button>
            ) : null}
            {desktopKeyboard ? (
              <div className="pos-search-keys">
                <div className="pos-search-letters">
                  {(searchLang === "ar" ? ARABIC_KEY_ROWS : ENGLISH_KEY_ROWS).map((row) => (
                    <div key={row.join("")} style={{ gridTemplateColumns: `repeat(${row.length}, minmax(0, 1fr))` }}>
                      {row.map((key) => (
                        <button key={key} type="button" onMouseDown={(event) => event.preventDefault()} onClick={() => writeSearch(key)}>{key}</button>
                      ))}
                    </div>
                  ))}
                  <div className="is-actions">
                    <button type="button" onMouseDown={(event) => event.preventDefault()} onClick={() => setSearchLang(searchLang === "ar" ? "en" : "ar")}>{searchLang === "ar" ? "EN" : "AR"}</button>
                    <button type="button" className="is-space" onMouseDown={(event) => event.preventDefault()} onClick={() => writeSearch(" ")}>مسافة</button>
                    <button type="button" onMouseDown={(event) => event.preventDefault()} onClick={backspaceSearch}>BS</button>
                    <button type="button" className="is-enter" aria-label="تنفيذ البحث" onMouseDown={(event) => event.preventDefault()} onClick={() => void searchItems(1)}>إدخال</button>
                  </div>
                </div>
                <div className="pos-search-num" dir="ltr">
                  <button type="button" className="is-del" onMouseDown={(event) => event.preventDefault()} onClick={() => placeSearchCaret("", 0)}>Del</button>
                  <button type="button" onMouseDown={(event) => event.preventDefault()} onClick={backspaceSearch}>BS</button>
                  {["7", "8", "9", "4", "5", "6", "1", "2", "3", "0"].map((key) => (
                    <button key={key} type="button" className={key === "0" ? "is-wide" : undefined} onMouseDown={(event) => event.preventDefault()} onClick={() => writeSearch(key)}>{key}</button>
                  ))}
                  <button type="button" className="is-enter" onMouseDown={(event) => event.preventDefault()} onClick={() => void searchItems(1)}>Enter</button>
                </div>
              </div>
            ) : null}
            <button className="primary-button" type="button" disabled={selectedResult < 0 || !shownResults[selectedResult]} onClick={() => { const item = shownResults[selectedResult]; if (item) void selectSearchItem(item); }}>إدخال</button>
          </form>
          </div>
        </div>
      ) : null}
      {priceEdit ? (
        <NumericEditModal
          key={`${priceEdit.lineId}-${priceEdit.kind}`}
          title={priceEdit.kind === "unit" ? "سعر الوحدة" : "الإجمالي"}
          ariaLabel={priceEdit.kind === "unit" ? "تعديل سعر الوحدة" : "تعديل الإجمالي"}
          inputLabel="السعر"
          initialValue={priceEdit.value}
          allowDecimal
          error={priceError}
          resetLabel="إعادة السعر الأصلي"
          onConfirm={confirmPrice}
          onCancel={() => setPriceEdit(null)}
          onReset={() => { if (cart) void persist(clearPriceOverride(cart, priceEdit.lineId)); setPriceEdit(null); }}
        />
      ) : null}
      {quantityTarget ? (
        <NumericEditModal
          key={quantityTarget}
          title="الكمية"
          ariaLabel="تعديل الكمية"
          inputLabel="الكمية"
          initialValue={quantityDraft}
          allowDecimal={false}
          error={quantityError}
          onConfirm={confirmQuantity}
          onCancel={() => setQuantityTarget(null)}
        />
      ) : null}
      {paying ? (
        <div className={finePointer ? "pos-overlay pos-checkout-overlay" : "pos-overlay pos-overlay-top pos-checkout-overlay"}>
          <form className="pos-modal pos-checkout-modal" role="dialog" aria-label="إتمام البيع" onSubmit={(event) => { event.preventDefault(); void submitCheckout(); }}>
            <button className="pos-checkout-close" type="button" aria-label="إغلاق" onClick={() => { setPaying(false); setMessage(""); }}>×</button>
            <header className="pos-checkout-head">
              <h3 className="pos-checkout-title">إتمام البيع</h3>
              <p className="pos-checkout-count">{totals.lineCount} أصناف</p>
              <div className="pos-checkout-total">
                <span>المبلغ الإجمالي</span>
                <strong>{formatPosMoney(totals.total)} <span className="pos-checkout-currency">₪</span></strong>
              </div>
            </header>
            <div className="pos-segment pos-checkout-methods" role="group" aria-label="طريقة الدفع">
              {(["Cash", "Card", "Split"] as const).map((method) => (
                <button key={method} type="button" className={payMethod === method ? "is-selected" : ""} onClick={() => setPayMethod(method)}>
                  {method === "Cash" ? "نقدي" : method === "Card" ? "بطاقة" : "مختلط"}
                </button>
              ))}
            </div>
            <div className="pos-checkout-body">
              <section className="pos-checkout-entry">
                {payMethod === "Card" ? <p className="pos-checkout-note">المبلغ على البطاقة: <strong className="pos-checkout-amount">{formatPosMoney(totals.total)} <span className="pos-checkout-currency">₪</span></strong></p> : null}
                {payMethod === "Cash" ? (
                  <label className={payField === "tendered" ? "pos-checkout-field is-active" : "pos-checkout-field"}>المبلغ المستلم
                    <input autoFocus={finePointer} aria-label="المبلغ المستلم" inputMode="decimal" value={tendered} onFocus={() => { setPayField("tendered"); setPayFresh(true); }} onChange={(event) => { setPayFresh(false); setTendered(event.target.value.replace(",", ".")); }} />
                  </label>
                ) : null}
                {payMethod === "Split" ? (
                  <>
                    <label className={payField === "cashPart" ? "pos-checkout-field is-active" : "pos-checkout-field"}>المبلغ النقدي
                      <input autoFocus={finePointer} aria-label="المبلغ النقدي" inputMode="decimal" value={cashPart} onFocus={() => { setPayField("cashPart"); setPayFresh(true); }} onChange={(event) => { setPayFresh(false); setCashPart(event.target.value.replace(",", ".")); }} />
                    </label>
                    <label className={payField === "tendered" ? "pos-checkout-field is-active" : "pos-checkout-field"}>المبلغ المستلم نقداً
                      <input aria-label="المبلغ المستلم نقداً" inputMode="decimal" value={tendered} onFocus={() => { setPayField("tendered"); setPayFresh(true); }} onChange={(event) => { setPayFresh(false); setTendered(event.target.value.replace(",", ".")); }} />
                    </label>
                    {isPositiveDecimal(cashPart) ? <p className="pos-checkout-note">البطاقة: <strong className="pos-checkout-amount">{formatPosMoney(subtractDecimals(totals.total, cashPart))} <span className="pos-checkout-currency">₪</span></strong></p> : null}
                  </>
                ) : null}
                {payMethod === "Cash" && isNonNegativeMoney(tendered) ? <p className="pos-checkout-change">الباقي للزبون: <strong className="pos-checkout-amount">{formatPosMoney(subtractDecimals(tendered, totals.total))} <span className="pos-checkout-currency">₪</span></strong></p> : null}
                {payMethod === "Split" && isNonNegativeMoney(tendered) && isPositiveDecimal(cashPart) ? <p className="pos-checkout-change">الباقي للزبون: <strong className="pos-checkout-amount">{formatPosMoney(subtractDecimals(tendered, cashPart))} <span className="pos-checkout-currency">₪</span></strong></p> : null}
                {message ? <p className="status-error">{message}</p> : null}
              </section>
              {showCheckoutKeypad ? (
                <div className="pos-checkout-pad">
                  <NumericKeypad
                    allowDecimal
                    onDigit={pushPayDigit}
                    onClear={() => {
                      if (payField === "cashPart") setCashPart("");
                      else setTendered("");
                      setPayFresh(false);
                    }}
                  />
                </div>
              ) : null}
            </div>
            <div className="pos-modal-actions pos-checkout-actions">
              <button className="nav-button" type="button" onClick={() => { setPaying(false); setMessage(""); }}>إلغاء</button>
              <button className="primary-button" type="submit" autoFocus={payMethod === "Card"} disabled={checkoutPending}>تأكيد الدفع</button>
            </div>
          </form>
        </div>
      ) : null}
      {opening ? (
        <div className="pos-overlay">
          <form className="pos-modal pos-shift" role="dialog" aria-label="بدء الوردية" onSubmit={(event) => { event.preventDefault(); void submitOpen(); }}>
            <h3>بدء الوردية</h3>
            <p>سيتم فتح وردية جديدة على هذا الكاونتر. فتح صندوق النقدية وبدء الوردية.</p>
            <p>الموظف: {user.displayName}</p>
            <p>الكاونتر: {session.stationName}</p>
            <label htmlFor="opening-cash">الرصيد الافتتاحي للصندوق</label>
            <input id="opening-cash" autoFocus inputMode="decimal" value={openingAmount} onChange={(event) => setOpeningAmount(event.target.value)} />
            {finePointer ? <NumericKeypad allowDecimal onDigit={(digit) => setOpeningAmount((current) => appendMoney(current, digit))} onClear={() => setOpeningAmount("")} /> : null}
            <p className="pos-help">هذا هو المبلغ النقدي الموجود فعليًا في الدرج عند بداية الوردية.</p>
            {cashMessage ? <p className="status-error">{cashMessage}</p> : null}
            <div className="pos-modal-actions">
              <button className="nav-button" type="button" onClick={() => setOpening(false)}>إلغاء</button>
              <button className="primary-button" type="submit">بدء الوردية</button>
            </div>
          </form>
        </div>
      ) : null}
      {closeBlocked ? (
        <div className="pos-overlay">
          <div className="pos-modal pos-shift" role="dialog" aria-label="سلّة غير مباعة">
            <h3>إغلاق الوردية</h3>
            <p>توجد أصناف في السلة لم يتم إتمام بيعها.</p>
            <div className="pos-modal-actions">
              <button className="nav-button" type="button" onClick={() => setCloseBlocked(false)}>إلغاء إغلاق الوردية</button>
              <button className="primary-button" type="button" onClick={() => setCloseBlocked(false)}>العودة للسلة</button>
            </div>
          </div>
        </div>
      ) : null}
      {closing && cashSession ? (
        <div className="pos-overlay">
          <form className="pos-modal pos-shift" role="dialog" aria-label="إغلاق الوردية" onSubmit={(event) => { event.preventDefault(); void submitClose(); }}>
            <h3>إغلاق الوردية</h3>
            <p>بدأت الوردية: {shiftClock(cashSession.openedAt)}</p>
            <p>الرصيد الافتتاحي: {cashSession.openingCashAmount} ₪</p>
            <p>المبيعات النقدية: {cashSales(cashSession.openingCashAmount, expectedPreview)} ₪</p>
            <p>المتوقع في الصندوق: {expectedPreview ?? cashSession.openingCashAmount} ₪</p>
            <label htmlFor="counted-cash">جرد الصندوق — المبلغ النقدي الفعلي</label>
            <input id="counted-cash" autoFocus inputMode="decimal" value={countedAmount} onChange={(event) => setCountedAmount(event.target.value)} />
            {finePointer ? <NumericKeypad allowDecimal onDigit={(digit) => setCountedAmount((current) => appendMoney(current, digit))} onClear={() => setCountedAmount("")} /> : null}
            {isNonNegativeMoney(countedAmount) ? <p>الفرق: {cashGap(expectedPreview ?? cashSession.openingCashAmount, countedAmount)}</p> : null}
            {cashMessage ? <p className="status-error">{cashMessage}</p> : null}
            <div className="pos-modal-actions">
              <button className="nav-button" type="button" onClick={() => setClosing(false)}>إلغاء</button>
              <button className="primary-button" type="submit">إغلاق الوردية</button>
            </div>
          </form>
        </div>
      ) : null}
      {closedSummary ? <p className="pos-help">المبلغ المتوقع {closedSummary.expectedCashAmount} | الفعلي {closedSummary.countedCashAmount} | الفرق {closedSummary.cashDifference}</p> : null}
    </section>
  );
}

function shiftClock(value: string) {
  const parsed = new Date(value);
  if (Number.isNaN(parsed.getTime())) return value;
  return parsed.toLocaleTimeString("en-GB", { hour: "2-digit", minute: "2-digit" });
}

function cashSales(opening: string, expected: string | null) {
  if (!expected) return "0";
  return subtractDecimals(expected, opening);
}

function cashGap(expected: string, counted: string) {
  const difference = subtractDecimals(counted, expected);
  if (Number(difference) === 0) return "متطابق";
  if (difference.startsWith("-")) return `عجز ${difference.slice(1)} ₪`;
  return `زيادة ${difference} ₪`;
}

function PriceBox({ value, manual, onClick }: { value: string; manual: boolean; onClick: () => void }) {
  return (
    <span>
      <button type="button" className="pos-money-value" onClick={onClick}>{value}</button>
      {manual ? <span className="pos-manual">تعديل يدوي</span> : null}
    </span>
  );
}

function QuantityStepper({
  label,
  quantity,
  onOpen,
  onStep,
}: {
  label: string;
  quantity: string;
  onOpen: () => void;
  onStep: (direction: "up" | "down") => void;
}) {
  return (
    <div className="quantity-stepper pos-qty" dir="ltr">
      <button type="button" aria-label={`إنقاص ${label}`} onClick={() => onStep("down")}>-</button>
      <button type="button" className="pos-qty-value" aria-label={`كمية ${label}`} onClick={onOpen}>{quantity}</button>
      <button type="button" aria-label={`زيادة ${label}`} onClick={() => onStep("up")}>+</button>
    </div>
  );
}

