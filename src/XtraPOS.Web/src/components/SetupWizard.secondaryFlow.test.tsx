import { useState } from "react";
import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { SetupPreview } from "./SetupPreview";
import { SetupWizard } from "./SetupWizard";
import { emptyBranchSetup, type BranchSetupInput } from "../types/setup";
import { OFFLINE_ACKNOWLEDGEMENT } from "../setup/offlineContract";
import { PREVIEW_ONE_BRANCH } from "../setup/setupPreview";
import { registerSecondaryDevice, secondaryRegistrationBody } from "../setup/secondaryDevice";
import * as deviceName from "../setup/deviceName";
import type { WizardBranchChoice } from "../setup/wizardFlow";

const STATION_ID = "11111111-1111-1111-1111-111111111111";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

function Harness({
  preview = false,
  branches = PREVIEW_ONE_BRANCH,
  onSubmit = () => undefined,
}: {
  preview?: boolean;
  branches?: WizardBranchChoice[];
  onSubmit?: (value: BranchSetupInput) => void;
}) {
  const [value, setValue] = useState<BranchSetupInput>({ ...emptyBranchSetup, deviceRole: "Secondary" });
  return (
    <SetupWizard
      preview={preview}
      value={value}
      onChange={setValue}
      onCancel={() => undefined}
      onSubmit={onSubmit}
      testConnection={async () => ({ ok: true, branches })}
    />
  );
}

function jsonResponse(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

describe("secondary device wizard", () => {
  it("walks preview from pairing through review without creating anything", async () => {
    const user = userEvent.setup();
    const fetchMock = vi.fn(() => {
      throw new Error("preview called the network");
    });
    vi.stubGlobal("fetch", fetchMock);
    render(<SetupPreview />);

    await user.click(screen.getByText("جهاز إضافي داخل فرع موجود"));
    await user.click(screen.getByRole("button", { name: "التالي" }));
    expect(screen.getByText("الخطوة 2")).toBeTruthy();
    await user.click(screen.getByText("البحث تلقائيًا على الشبكة المحلية"));
    await user.click(screen.getByRole("button", { name: "خادم يعيد فرعًا واحدًا" }));
    await user.click(screen.getByRole("button", { name: "التالي" }));

    expect(screen.getByRole("heading", { name: "السماح بربط هذا الجهاز" })).toBeTruthy();
    expect(screen.getByText(/يتم إنشاء رمز الربط من خادم الفرع بواسطة مستخدم مخول/)).toBeTruthy();
    expect(screen.getByText("الرمز مؤقت ويُستخدم مرة واحدة.")).toBeTruthy();
    expect(screen.getByLabelText("رمز ربط الجهاز")).toBeTruthy();
    expect(screen.getByRole("button", { name: "التالي" })).toHaveProperty("disabled", true);
    await user.type(screen.getByLabelText("رمز ربط الجهاز"), "AB12CD34");
    await user.click(screen.getByRole("button", { name: "التحقق من الرمز" }));
    expect(screen.getByText("تم التحقق من رمز الربط. لم يُرسل أي طلب.")).toBeTruthy();
    await user.click(screen.getByRole("button", { name: "التالي" }));

    expect(screen.getByRole("heading", { name: "اختيار نقطة البيع" })).toBeTruthy();
    expect(screen.getByText("كاونتر 1")).toBeTruthy();
    expect(screen.getByText("كاونتر 2")).toBeTruthy();
    expect(screen.getByText("إنشاء نقطة بيع جديدة")).toBeTruthy();
    expect(screen.getByText("لن يتم إنشاء نقطة بيع فعلية")).toBeTruthy();
    await user.click(screen.getByText("كاونتر 2"));
    await user.click(screen.getByRole("button", { name: "التالي" }));

    expect(screen.getByRole("heading", { name: "تعريف هذا الجهاز" })).toBeTruthy();
    expect(screen.getByText("اسم يساعدك على تمييز هذا الجهاز داخل الفرع.")).toBeTruthy();
    const deviceName = screen.getByLabelText("اسم الجهاز");
    expect(deviceName).toHaveProperty("value", "");
    await user.type(deviceName, "Desktop1");
    await user.clear(deviceName);
    await user.type(deviceName, "صندوق جانبي");
    await user.click(screen.getByRole("button", { name: "التالي" }));

    expect(screen.getByText(/IndexedDB/)).toBeTruthy();
    expect(screen.getByText(/PendingSales/)).toBeTruthy();
    expect(screen.getByText(OFFLINE_ACKNOWLEDGEMENT)).toBeTruthy();
    expect(screen.getByText("لن يتم تنزيل أو حفظ بيانات في وضع المعاينة.")).toBeTruthy();
    expect(screen.getByRole("button", { name: "التالي" })).toHaveProperty("disabled", true);
    await user.click(screen.getByRole("checkbox"));
    await user.click(screen.getByRole("button", { name: "التالي" }));

    expect(screen.getByRole("heading", { name: "مراجعة ربط الجهاز" })).toBeTruthy();
    expect(screen.getByText("اسم الفرع: Shop1")).toBeTruthy();
    expect(screen.getByText("خادم الفرع: Shop1")).toBeTruthy();
    expect(screen.getByText("العنوان: localhost")).toBeTruthy();
    expect(screen.getByText("المنفذ: 5080")).toBeTruthy();
    expect(screen.getByText("نقطة البيع: كاونتر 2")).toBeTruthy();
    expect(screen.getByText("اسم الجهاز: صندوق جانبي")).toBeTruthy();
    expect(screen.getByText("العمل دون اتصال: مفعّل")).toBeTruthy();
    expect(screen.getByText("تم التحقق من إذن ربط الجهاز")).toBeTruthy();
    expect(screen.queryByText("AB12CD34")).toBeNull();
    expect(screen.getByText("Secondary Device")).toBeTruthy();
    expect(screen.getByText("Branch API / Shop1")).toBeTruthy();
    expect(screen.queryByText(/password|connectionString|XtraPOS_Branch|اسم قاعدة البيانات/)).toBeNull();
    expect(screen.getByRole("button", { name: "إنهاء المعاينة" })).toBeTruthy();
    expect(screen.queryByRole("button", { name: "ربط هذا الجهاز" })).toBeNull();
    await user.click(screen.getByRole("button", { name: "إنهاء المعاينة" }));
    expect(screen.getByText("انتهت المعاينة")).toBeTruthy();
    expect(screen.getByText("لم يتم حفظ أو تنفيذ أي تغيير.")).toBeTruthy();
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("rejects an invalid pairing code and keeps registration closed", async () => {
    const user = userEvent.setup();
    const fetchMock = vi.fn(async (url: string) => {
      if (String(url).includes("/api/devices/pairing-codes/verify")) {
        return jsonResponse({ message: "رمز الربط غير صالح أو انتهت صلاحيته." }, 400);
      }
      throw new Error(String(url));
    });
    vi.stubGlobal("fetch", fetchMock);
    render(<Harness />);
    await user.click(screen.getByRole("button", { name: "التالي" }));
    await user.click(screen.getByText("البحث تلقائيًا على الشبكة المحلية"));
    await user.click(screen.getByRole("button", { name: "البحث الآن" }));
    await user.click(screen.getByRole("button", { name: "التالي" }));
    await user.type(screen.getByLabelText("رمز ربط الجهاز"), "EXPIRED");
    await user.click(screen.getByRole("button", { name: "التحقق من الرمز" }));
    expect(screen.getByText("رمز الربط غير صالح أو انتهت صلاحيته.")).toBeTruthy();
    expect(screen.getByRole("button", { name: "التالي" })).toHaveProperty("disabled", true);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(String(fetchMock.mock.calls[0][0])).toContain("/api/devices/pairing-codes/verify");
    expect(String(fetchMock.mock.calls[0][0])).not.toContain("create-branch");
  });

  it("asks the branch API before creating a point of sale and stops without permission", async () => {
    const user = userEvent.setup();
    const calls: string[] = [];
    vi.stubGlobal("fetch", vi.fn(async (url: string, init?: RequestInit) => {
      const href = String(url);
      calls.push(`${init?.method ?? "GET"} ${href}`);
      if (href.includes("/pairing-codes/verify")) {
        return jsonResponse({ pairingToken: "pair-token" });
      }
      if (href.includes("/api/devices/stations") && (init?.method ?? "GET") !== "POST") {
        return jsonResponse([{ stationId: STATION_ID, stationNumber: 1, stationName: "كاونتر 1" }]);
      }
      if (href.includes("/api/devices/stations")) {
        return jsonResponse({ message: "لا توجد صلاحية لإنشاء نقطة بيع." }, 403);
      }
      throw new Error(href);
    }));
    render(<Harness />);
    await user.click(screen.getByRole("button", { name: "التالي" }));
    await user.click(screen.getByText("إدخال عنوان الخادم يدويًا"));
    await user.type(screen.getByLabelText("الخادم / عنوان IP"), "192.168.0.10");
    await user.click(screen.getByRole("button", { name: "اختبار الاتصال" }));
    await user.click(screen.getByRole("button", { name: "التالي" }));
    await user.type(screen.getByLabelText("رمز ربط الجهاز"), "AB12CD34");
    await user.click(screen.getByRole("button", { name: "التحقق من الرمز" }));
    await user.click(screen.getByRole("button", { name: "التالي" }));
    expect(screen.getByRole("heading", { name: "اختيار نقطة البيع" })).toBeTruthy();
    expect(screen.getByText("كاونتر 1")).toBeTruthy();
    await user.click(screen.getByText("إنشاء نقطة بيع جديدة"));
    await user.type(screen.getByLabelText("رقم نقطة البيع"), "3");
    await user.type(screen.getByLabelText("اسم نقطة البيع"), "كاونتر 3");
    await user.click(screen.getByRole("button", { name: "التالي" }));
    expect(screen.getByText("لا توجد صلاحية لإنشاء نقطة بيع.")).toBeTruthy();
    expect(screen.getByRole("heading", { name: "اختيار نقطة البيع" })).toBeTruthy();
    expect(calls.some((call) => call.startsWith("POST http://192.168.0.10:5080/api/devices/stations"))).toBe(true);
    expect(calls.some((call) => call.includes("create-branch") || call.includes("connectionString"))).toBe(false);
  });

  it("suggests a host device name, keeps it editable, and reviews the device apart from the point of sale", async () => {
    const user = userEvent.setup();
    vi.spyOn(deviceName, "readHostDeviceLabel").mockReturnValue("Desktop1");
    vi.stubGlobal("fetch", vi.fn(async (url: string) => {
      if (String(url).includes("/pairing-codes/verify")) {
        return jsonResponse({ pairingToken: "pair-token" });
      }
      return jsonResponse([{ stationId: STATION_ID, stationNumber: 1, stationName: "كاونتر 1" }]);
    }));
    render(<Harness />);
    await user.click(screen.getByRole("button", { name: "التالي" }));
    await user.click(screen.getByText("البحث تلقائيًا على الشبكة المحلية"));
    await user.click(screen.getByRole("button", { name: "البحث الآن" }));
    await user.click(screen.getByRole("button", { name: "التالي" }));
    await user.type(screen.getByLabelText("رمز ربط الجهاز"), "AB12CD34");
    await user.click(screen.getByRole("button", { name: "التحقق من الرمز" }));
    await user.click(screen.getByRole("button", { name: "التالي" }));
    await user.click(screen.getByText("كاونتر 1"));
    await user.click(screen.getByRole("button", { name: "التالي" }));
    const nameInput = screen.getByLabelText("اسم الجهاز");
    expect(nameInput).toHaveProperty("value", "Desktop1");
    await user.clear(nameInput);
    await user.type(nameInput, "صندوق جانبي");
    await user.click(screen.getByRole("button", { name: "التالي" }));
    await user.click(screen.getByRole("checkbox"));
    await user.click(screen.getByRole("button", { name: "التالي" }));
    expect(screen.getByText("نقطة البيع: كاونتر 1")).toBeTruthy();
    expect(screen.getByText("اسم الجهاز: صندوق جانبي")).toBeTruthy();
    expect(screen.getByText("العمل دون اتصال: مفعّل")).toBeTruthy();
  });

  it("links the device through the branch API only", async () => {
    const user = userEvent.setup();
    const calls: Array<{ url: string; body: string }> = [];
    vi.stubGlobal("fetch", vi.fn(async (url: string, init?: RequestInit) => {
      const href = String(url);
      calls.push({ url: href, body: String(init?.body ?? "") });
      if (href.includes("/pairing-codes/verify")) {
        return jsonResponse({ pairingToken: "pair-token" });
      }
      if (href.endsWith("/api/devices/register")) {
        return jsonResponse({
          deviceId: "22222222-2222-2222-2222-222222222222",
          businessId: "33333333-3333-3333-3333-333333333333",
          branchId: "44444444-4444-4444-4444-444444444444",
          stationId: STATION_ID,
          deviceName: "Desktop1",
          deviceType: "POS",
          deviceToken: "device-token",
          registeredAt: "2026-10-07T00:00:00.000Z",
          businessName: "منشأة",
          branchName: "Shop1",
          branchType: "Retail",
          stationName: "كاونتر 1",
          stationNumber: 1,
        });
      }
      return jsonResponse([{ stationId: STATION_ID, stationNumber: 1, stationName: "كاونتر 1" }]);
    }));
    let submitted: BranchSetupInput | null = null;
    render(<Harness onSubmit={(value) => { submitted = value; }} />);
    await user.click(screen.getByRole("button", { name: "التالي" }));
    await user.click(screen.getByText("إدخال عنوان الخادم يدويًا"));
    await user.type(screen.getByLabelText("الخادم / عنوان IP"), "192.168.0.10");
    await user.click(screen.getByRole("button", { name: "اختبار الاتصال" }));
    await user.click(screen.getByRole("button", { name: "التالي" }));
    await user.type(screen.getByLabelText("رمز ربط الجهاز"), "AB12CD34");
    await user.click(screen.getByRole("button", { name: "التحقق من الرمز" }));
    await user.click(screen.getByRole("button", { name: "التالي" }));
    await user.click(screen.getByText("كاونتر 1"));
    await user.click(screen.getByRole("button", { name: "التالي" }));
    await user.type(screen.getByLabelText("اسم الجهاز"), "Desktop1");
    await user.click(screen.getByRole("button", { name: "التالي" }));
    await user.click(screen.getByRole("checkbox"));
    await user.click(screen.getByRole("button", { name: "التالي" }));
    expect(screen.getByRole("button", { name: "ربط هذا الجهاز" })).toBeTruthy();
    await user.click(screen.getByRole("button", { name: "ربط هذا الجهاز" }));
    expect(submitted).not.toBeNull();
    const draft = submitted!;
    expect(draft.password).toBe("");
    expect(draft.centralPassword).toBe("");
    const body = secondaryRegistrationBody(draft);
    expect(Object.keys(body).sort()).toEqual(["branchId", "deviceName", "pairingToken", "stationId"]);
    expect(JSON.stringify(body)).not.toMatch(/password|connectionString|Initial Catalog|User ID|databaseName/i);
    await expect(registerSecondaryDevice({ ...draft, pairingVerified: false, pairingToken: "" })).rejects.toThrow(
      "يجب التحقق من رمز ربط الجهاز قبل تسجيله.",
    );
    const session = await registerSecondaryDevice(draft);
    expect(session.deviceName).toBe("Desktop1");
    expect(session.stationName).toBe("كاونتر 1");
    expect(session.deviceName).not.toBe(session.stationName);
    const registration = calls.find((call) => call.url.endsWith("/api/devices/register"));
    expect(registration?.url).toBe("http://192.168.0.10:5080/api/devices/register");
    expect(registration?.body).not.toMatch(/password|connectionString|Initial Catalog|User ID|databaseName/i);
    expect(calls.some((call) => call.url.includes("/api/setup/create-branch"))).toBe(false);
  });
});
