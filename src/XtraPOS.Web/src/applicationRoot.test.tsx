import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { ApplicationRoot } from "./ApplicationRoot";
import { isSetupPreviewEnabled, SETUP_PREVIEW_PATH } from "./setup/setupPreview";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  localStorage.clear();
});

function rejectApi() {
  const fetchMock = vi.fn(() => Promise.reject(new Error("API unavailable")));
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
    expect(String(fetchMock.mock.calls[0]?.[0])).toContain("/api/setup/status");
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
