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

  it("opens login after setup and then the home page", async () => {
    const user = userEvent.setup();
    const device = {
      deviceId: "11111111-1111-1111-1111-111111111111",
      businessId: "22222222-2222-2222-2222-222222222222",
      branchId: "33333333-3333-3333-3333-333333333333",
      stationId: "44444444-4444-4444-4444-444444444444",
      deviceName: "صندوق 1",
      deviceType: "POS",
      deviceToken: "device-token",
      registeredAt: "2026-11-13T00:00:00",
      businessName: "منشأة الشربل",
      branchName: "الفرع الرئيسي",
      branchType: "Retail",
      stationName: "كاونتر 1",
      stationNumber: 1,
    };
    vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url.includes("47321")) {
        return new Response("", { status: 404 });
      }
      if (url.includes("/api/setup/status")) {
        return new Response(JSON.stringify({ status: "Configured" }), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        });
      }
      if (url.includes("/api/device/local-registration")) {
        return new Response(JSON.stringify(device), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        });
      }
      if (url.includes("/api/auth/login") && init?.method === "POST") {
        return new Response(JSON.stringify({
          userId: "55555555-5555-5555-5555-555555555555",
          username: "admin",
          displayName: "مدير",
          roles: [],
          permissions: ["Items.View"],
          businessName: "منشأة الشربل",
          branchName: "الفرع الرئيسي",
          stationName: "كاونتر 1",
          deviceName: "صندوق 1",
          mustChangePassword: false,
        }), { status: 200, headers: { "Content-Type": "application/json" } });
      }
      return new Response("", { status: 404 });
    }));

    render(<ApplicationRoot development pathname="/" />);

    expect(await screen.findByRole("button", { name: "تسجيل الدخول" })).toBeTruthy();
    await user.type(screen.getByLabelText("اسم المستخدم"), "admin");
    await user.type(screen.getByLabelText("كلمة المرور"), "secret");
    await user.click(screen.getByRole("button", { name: "تسجيل الدخول" }));
    expect(await screen.findByRole("button", { name: "نقطة البيع" })).toBeTruthy();
  });
});
