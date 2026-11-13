import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { ApplicationRoot } from "./ApplicationRoot";
import { isSetupPreviewEnabled, SETUP_PREVIEW_PATH } from "./setup/setupPreview";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  localStorage.clear();
  sessionStorage.clear();
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

  it("shows configured device details when the local branch agent reports an already-configured branch", async () => {
    vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes("127.0.0.1:47331/status")) {
        return new Response(JSON.stringify({
          product: "XtraPOS.BranchAgent",
          version: "0.3.0",
          machineName: "DESKTOP-MFTEQK0",
          paired: true,
          server: "https://test-pos.sharbelfashion.com",
          agentId: "agent-1",
          instances: ["localhost"],
          sqlAvailable: true,
          configuredBranch: {
            businessName: "منشأة الشربل",
            branchName: "الفرع الرئيسي",
            branchCode: "001",
            databaseName: "XtraPOS_Branch",
            server: "localhost",
            deviceName: "POS-01",
            stationNumber: 1,
            stationName: "كاونتر 1",
          },
        }), { status: 200, headers: { "Content-Type": "application/json" } });
      }
      return new Response(JSON.stringify({ status: "NotConfigured" }), {
        status: 200,
        headers: { "Content-Type": "application/json" },
      });
    }));

    render(<ApplicationRoot development pathname="/" />);

    expect(await screen.findByText("تم إعداد هذا الجهاز مسبقًا")).toBeTruthy();
    expect(screen.getByText(/منشأة الشربل/)).toBeTruthy();
    expect(screen.getByText(/الفرع الرئيسي — 001/)).toBeTruthy();
    expect(screen.getByText(/XtraPOS_Branch/)).toBeTruthy();
    await userEvent.click(screen.getByRole("button", { name: "إعداد فرع أو جهاز آخر" }));
    expect(await screen.findByRole("heading", { name: "مرحبًا بك في XtraPOS" })).toBeTruthy();
  });

  it("redirects automatically to localAppUrl when the local branch agent has a configured branch and local web host", async () => {
    vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes("127.0.0.1:47331/status")) {
        return new Response(JSON.stringify({
          product: "XtraPOS.BranchAgent",
          version: "0.3.0",
          machineName: "DESKTOP-MFTEQK0",
          paired: true,
          server: "https://test-pos.sharbelfashion.com",
          agentId: "agent-1",
          instances: ["localhost"],
          sqlAvailable: true,
          configuredBranch: {
            businessName: "SW",
            branchName: "Lab",
            branchCode: "11",
            databaseName: "XtraPOS_Branch2",
            server: "localhost",
            deviceName: "صندوق 1",
            stationNumber: 1,
            stationName: "كاونتر 1",
          },
          localAppUrl: "http://127.0.0.1:47335",
        }), { status: 200, headers: { "Content-Type": "application/json" } });
      }
      return new Response(JSON.stringify({ status: "NotConfigured" }), {
        status: 200,
        headers: { "Content-Type": "application/json" },
      });
    }));

    const onNavigate = vi.fn();
    render(<ApplicationRoot development pathname="/" onNavigate={onNavigate} />);

    await vi.waitFor(() => {
      expect(onNavigate).toHaveBeenCalledWith("http://127.0.0.1:47335");
    });
  });

  it("stays on the configured details screen when ?setup=1 is present and lets the user open the local app manually", async () => {
    window.history.replaceState(null, "", "/?setup=1");
    vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes("127.0.0.1:47331/status")) {
        return new Response(JSON.stringify({
          product: "XtraPOS.BranchAgent",
          version: "0.3.0",
          machineName: "DESKTOP-MFTEQK0",
          paired: true,
          server: "https://test-pos.sharbelfashion.com",
          agentId: "agent-1",
          instances: ["localhost"],
          sqlAvailable: true,
          configuredBranch: {
            businessName: "SW",
            branchName: "Lab",
            branchCode: "11",
            databaseName: "XtraPOS_Branch2",
            server: "localhost",
            deviceName: "صندوق 1",
            stationNumber: 1,
            stationName: "كاونتر 1",
          },
          localAppUrl: "http://127.0.0.1:47335",
        }), { status: 200, headers: { "Content-Type": "application/json" } });
      }
      return new Response(JSON.stringify({ status: "NotConfigured" }), {
        status: 200,
        headers: { "Content-Type": "application/json" },
      });
    }));

    const onNavigate = vi.fn();
    try {
      render(<ApplicationRoot development pathname="/" onNavigate={onNavigate} />);

      expect(await screen.findByText("تم إعداد هذا الجهاز مسبقًا")).toBeTruthy();
      expect(onNavigate).not.toHaveBeenCalled();

      await userEvent.click(screen.getByRole("button", { name: "فتح التطبيق الرئيسي" }));
      expect(onNavigate).toHaveBeenCalledWith("http://127.0.0.1:47335");
    } finally {
      window.history.replaceState(null, "", "/");
    }
  });
});
