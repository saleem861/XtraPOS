// نشاط الانتظار العام: طلبات /api التي بدأت بعد تفاعل المستخدم بقليل (فتح شاشة، حفظ، بحث)
// تُعدّ «انتظاراً يراه المستخدم». الطلبات الخلفية الدورية لا تُعدّ، فلا يومض المؤشر وحده.
type Listener = () => void;

const INTERACTION_WINDOW_MS = 1500;
let lastInteraction = 0;
let userRequests = 0;
let localWaits = 0;
const listeners = new Set<Listener>();

function emit() {
  for (const listener of listeners) listener();
}

export function subscribeWaiting(listener: Listener) {
  listeners.add(listener);
  return () => { listeners.delete(listener); };
}

export function waitingSnapshot() {
  return { userRequests, localWaits };
}

// مؤشر داخل الشاشة ظاهر: المؤشر العام يختفي حتى لا يتكرر.
export function registerLocalWait() {
  localWaits += 1;
  emit();
  return () => {
    localWaits = Math.max(0, localWaits - 1);
    emit();
  };
}

function requestUrl(input: RequestInfo | URL) {
  return typeof input === "string" ? input : input instanceof URL ? input.href : input.url;
}

function isApiRequest(input: RequestInfo | URL) {
  const url = requestUrl(input);
  return url.startsWith("/api/") || url.includes(`${window.location.origin}/api/`);
}

/** ترويسة تعلّم طلباً دورياً في الخلفية، فلا يُعدّ انتظاراً يراه المستخدم مهما كان توقيته. */
export const BACKGROUND_HEADER = "X-XtraPOS-Background";

/** طلبات دورية معروفة (استطلاع رمز العضوية كل ثانية) لا تُعدّ أبداً، حتى لو بدأت بعد ضغطة مباشرة. */
const BACKGROUND_PATHS = ["/api/pos/membership-counter", "/health"];

export function isBackgroundRequest(input: RequestInfo | URL, init?: RequestInit) {
  const url = requestUrl(input);
  if (BACKGROUND_PATHS.some((path) => url.startsWith(path) || url.includes(`${window.location.origin}${path}`))) return true;
  const headers = new Headers(init?.headers ?? (input instanceof Request ? input.headers : undefined));
  return headers.get(BACKGROUND_HEADER) === "1";
}

// يلفّ fetch مرة واحدة ويعيد دالة الإزالة.
export function installWaitingActivity() {
  const markInteraction = () => { lastInteraction = Date.now(); };
  window.addEventListener("pointerdown", markInteraction, true);
  window.addEventListener("keydown", markInteraction, true);
  const original = window.fetch;
  const wrapped: typeof window.fetch = (input, init) => {
    const counted = Date.now() - lastInteraction <= INTERACTION_WINDOW_MS && isApiRequest(input) && !isBackgroundRequest(input, init);
    if (!counted) return original(input, init);
    userRequests += 1;
    emit();
    const done = () => {
      userRequests = Math.max(0, userRequests - 1);
      emit();
    };
    return original(input, init).then(
      (response) => { done(); return response; },
      (error: unknown) => { done(); throw error; },
    );
  };
  window.fetch = wrapped;
  return () => {
    window.removeEventListener("pointerdown", markInteraction, true);
    window.removeEventListener("keydown", markInteraction, true);
    if (window.fetch === wrapped) window.fetch = original;
  };
}
