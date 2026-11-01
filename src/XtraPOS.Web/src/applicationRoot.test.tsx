import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { ApplicationRoot } from "./ApplicationRoot";
import { isSetupPreviewEnabled, SETUP_PREVIEW_PATH } from "./setup/setupPreview";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  localStorage.clear();
});

function rejectApi() {
  const fetchMock = vi.fn((_input: RequestInfo | URL, _init?: RequestInit) => Promise.reject(new Error("API unavailable")));
  vi.stubGlobal("fetch", fetchMock);
  return fetchMock;
}

describe("setup preview route gate", () => {
  it("renders the preview when the API is unavailable", async () => {
    const fetchMock = rejectApi();

    render(<ApplicationRoot development pathname={SETUP_PREVIEW_PATH} />);

    expect((await screen.findByRole("status")).textContent).toContain("وضع معاينة — لن يتم حفظ أو تنفيذ أي تغييرات");
    expect(screen.getByRole("heading", { name: "إعداد XtraPOS" })).toBeTruthy();
    expect(screen.queryByText("تعذر الاتصال بالخادم. تأكد أن التطبيق يعمل ثم أعد المحاولة.")).toBeNull();
    expect(screen.queryByRole("heading", { name: "تسجيل الدخول" })).toBeNull();
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("does not require a device session or login", async () => {
    const fetchMock = rejectApi();
    localStorage.setItem("xtrapos.deviceSession", "{not-a-session}");

    render(<ApplicationRoot development pathname={SETUP_PREVIEW_PATH} />);

    expect(await screen.findByRole("heading", { name: "إعداد XtraPOS" })).toBeTruthy();
    expect(screen.queryByRole("heading", { name: "تسجيل الدخول" })).toBeNull();
    expect(screen.queryByText("تم إعداد الفرع على هذا الخادم، وهذا الجهاز غير مرتبط به بعد")).toBeNull();
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("still checks the API on a normal route", async () => {
    const fetchMock = rejectApi();

    render(<ApplicationRoot development pathname="/" />);

    expect(await screen.findByText("تعذر الاتصال بالخادم. تأكد أن التطبيق يعمل ثم أعد المحاولة.")).toBeTruthy();
    expect(screen.queryByText("وضع معاينة — لن يتم حفظ أو تنفيذ أي تغييرات")).toBeNull();
    expect(fetchMock).toHaveBeenCalled();
    // Background sync status may be requested before setup status.
    expect(fetchMock.mock.calls.some(([url]) => String(url).includes("/api/setup/status"))).toBe(true);
  });

  it("opens a new organization wizard from the main setup screen when nothing is configured", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ status: "NotConfigured" }), {
      status: 200,
      headers: { "Content-Type": "application/json" },
    })));

    render(<ApplicationRoot development pathname="/" />);

    expect(await screen.findByRole("heading", { name: "مرحبًا بك في XtraPOS" })).toBeTruthy();
    expect(screen.queryByRole("button", { name: "ربط هذا الجهاز بالفرع الحالي" })).toBeNull();
    await userEvent.click(screen.getByRole("button", { name: /فرع مستقل/ }));
    await userEvent.click(screen.getByRole("button", { name: "البدء بالإعداد" }));
    expect(await screen.findByText("هل يتبع إدارة مركزية؟")).toBeTruthy();
    expect(screen.queryByText("المنظمة موجودة على هذا الجهاز")).toBeNull();
  });

  it("does not expose setup-preview in production", async () => {
    const fetchMock = rejectApi();

    expect(isSetupPreviewEnabled(false, SETUP_PREVIEW_PATH)).toBe(false);
    render(<ApplicationRoot development={false} pathname={SETUP_PREVIEW_PATH} />);

    expect(await screen.findByText("تعذر الاتصال بالخادم. تأكد أن التطبيق يعمل ثم أعد المحاولة.")).toBeTruthy();
    expect(screen.queryByText("وضع معاينة — لن يتم حفظ أو تنفيذ أي تغييرات")).toBeNull();
    expect(fetchMock).toHaveBeenCalled();
  });
});
