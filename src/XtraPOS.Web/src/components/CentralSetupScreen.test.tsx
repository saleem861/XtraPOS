import { useState } from "react";
import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { CentralSetupScreen } from "./CentralSetupScreen";
import { centralSetupPayload } from "../setup/centralSetup";
import { SetupWizard } from "./SetupWizard";
import { emptyBranchSetup, type BranchSetupInput, type DeviceSession } from "../types/setup";

const session = {
  deviceId: "11111111-1111-1111-1111-111111111111",
  businessId: "22222222-2222-2222-2222-222222222222",
  branchId: "33333333-3333-3333-3333-333333333333",
  stationId: "44444444-4444-4444-4444-444444444444",
  deviceName: "الإدارة",
  deviceType: "POS",
  deviceToken: "device-token",
  registeredAt: "2026-10-07T00:00:00.000Z",
  businessName: "منشأة",
  branchName: "الفرع",
  branchType: "Retail",
  stationName: "كاونتر 1",
  stationNumber: 1,
} as DeviceSession;

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe("real central setup", () => {
  it("opens the shared wizard from settings and sends the setup to the branch API", async () => {
    const user = userEvent.setup();
    const calls: Array<{ url: string; body: string }> = [];
    vi.stubGlobal("fetch", vi.fn(async (url: string, init?: RequestInit) => {
      const href = String(url);
      calls.push({ url: href, body: String(init?.body ?? "") });
      if (href.includes("/api/setup/test-connection")) {
        return new Response(JSON.stringify({ ok: true, databaseRole: "Central", databaseExists: false, nameAvailable: true }), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        });
      }
      const stream = [
        { type: "progress", stage: "connect", state: "succeeded", label: "تم الاتصال بالخادم" },
        { type: "progress", stage: "createDatabase", state: "succeeded", label: "تم إنشاء قاعدة البيانات" },
        { type: "progress", stage: "migrations", state: "succeeded", label: "تم تطبيق بنية XtraPOS Central" },
        { type: "progress", stage: "business", state: "succeeded", label: "تم إنشاء هوية المنشأة" },
        { type: "progress", stage: "verify", state: "succeeded", label: "تم التحقق من القاعدة المركزية" },
        { type: "completed", alreadyConfigured: false, businessName: "منشأة المركز" },
      ].map((line) => JSON.stringify(line)).join("\n");
      return new Response(stream, { status: 200, headers: { "Content-Type": "application/x-ndjson" } });
    }));

    render(<CentralSetupScreen session={session} onHome={() => undefined} />);
    expect(screen.getByRole("heading", { name: "الإعدادات" })).toBeTruthy();
    await user.click(screen.getByRole("button", { name: "الإدارة المركزية" }));
    expect(screen.getByRole("heading", { name: "الإدارة المركزية" })).toBeTruthy();
    await user.click(screen.getByRole("button", { name: "إعداد الإدارة المركزية" }));
    expect(screen.getByRole("heading", { name: "إعداد الإدارة المركزية" })).toBeTruthy();
    expect(screen.queryByRole("button", { name: "محاكاة نجاح الاتصال" })).toBeNull();
    await user.click(screen.getByText("إنشاء قاعدة مركزية جديدة"));
    await user.click(screen.getByRole("button", { name: "التالي" }));
    await user.click(screen.getByText("على هذا الجهاز"));
    await user.click(screen.getByRole("button", { name: "التالي" }));
    expect(screen.getByRole("button", { name: "اختبار الاتصال" })).toBeTruthy();
    await user.click(screen.getByRole("button", { name: "اختبار الاتصال" }));
    await user.click(screen.getByRole("button", { name: "التالي" }));
    await user.type(screen.getByLabelText("اسم المنشأة"), "منشأة المركز");
    await user.click(screen.getByRole("button", { name: "التالي" }));
    expect(screen.getByText("اسم قاعدة البيانات: XtraPOS_Central")).toBeTruthy();
    expect(screen.queryByText(/كلمة المرور|Password=/)).toBeNull();
    expect(screen.getByRole("button", { name: "إنشاء القاعدة المركزية" })).toBeTruthy();
    await user.click(screen.getByRole("button", { name: "إنشاء القاعدة المركزية" }));
    expect(await screen.findByText("تم إعداد الإدارة المركزية بنجاح")).toBeTruthy();
    expect(screen.getByText("✓ تم التحقق من القاعدة المركزية")).toBeTruthy();
    const setup = calls.find((call) => call.url === "/api/central/setup");
    expect(setup).toBeTruthy();
    expect(setup?.body).not.toMatch(/connectionString|Password=/i);
    const body = JSON.parse(setup!.body) as { password: string | null; databaseName: string };
    expect(body.databaseName).toBe("XtraPOS_Central");
    expect(body.password).toBeNull();
  });

  it("keeps the central review free of the SQL password", () => {
    const draft: BranchSetupInput = {
      ...emptyBranchSetup,
      deviceRole: "CentralOnly",
      centralIntent: "new",
      centralLocation: "Remote",
      centralServer: "10.0.0.8",
      centralDatabaseName: "XtraPOS_Central",
      centralUsername: "central-user",
      centralPassword: "Secret-Central-99",
      businessName: "منشأة المركز",
      centralConnectionTested: true,
    };
    function Harness() {
      const [value, setValue] = useState(draft);
      return (
        <SetupWizard
          value={value}
          onChange={setValue}
          onCancel={() => undefined}
          onSubmit={() => undefined}
          initialIndex={5}
        />
      );
    }
    render(<Harness />);
    expect(screen.getByRole("heading", { name: "مراجعة الإدارة المركزية" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "إنشاء القاعدة المركزية" })).toBeTruthy();
    expect(screen.queryByText("Secret-Central-99")).toBeNull();
    expect(screen.queryByText("central-user")).toBeNull();
    const payload = centralSetupPayload(draft);
    expect(payload.password).toBe("Secret-Central-99");
    expect(JSON.stringify(payload)).not.toMatch(/connectionString/i);
    expect(Object.keys(payload)).not.toContain("connectionString");
  });
});
