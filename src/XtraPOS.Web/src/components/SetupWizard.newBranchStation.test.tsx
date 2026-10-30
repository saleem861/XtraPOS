import { useState } from "react";
import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { createBranch } from "../api/client";
import { visibleSteps } from "../setup/wizardFlow";
import { emptyBranchSetup, type BranchSetupInput } from "../types/setup";
import { SetupWizard } from "./SetupWizard";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const newBranch: BranchSetupInput = {
  ...emptyBranchSetup,
  deviceRole: "MainBranch",
  operatingMode: "CentralManagement",
  centralIntent: "existing",
  centralLocation: "Local",
  centralAction: "existing",
  centralDatabaseName: "XtraPOS_Central",
  centralConnectionTested: true,
  linkToCentral: "yes",
  businessName: "منشأة المعاينة",
  branchIntent: "new",
  branchLocation: "Local",
  branchDatabaseName: "XtraPOS_Branch_02",
  branchConnectionTested: true,
  branchName: "فرع جديد",
  branchCode: "02",
  branchType: "Retail",
  branchRemoteChoice: "no",
};

const existingBranch: BranchSetupInput = {
  ...newBranch,
  branchIntent: "existing",
  branchDatabaseName: "XtraPOS_Branch",
  stationNumber: "",
  stationName: "",
};

function Harness({
  initial,
  onSubmit = () => undefined,
}: {
  initial: BranchSetupInput;
  onSubmit?: (value: BranchSetupInput) => void;
}) {
  const [value, setValue] = useState(initial);
  return (
    <SetupWizard
      value={value}
      onChange={setValue}
      onCancel={() => undefined}
      onSubmit={onSubmit}
      initialIndex={visibleSteps(initial).indexOf("remoteCopy")}
    />
  );
}

describe("new branch station draft", () => {
  it("does not read stations while choosing remote copy or before the branch exists", async () => {
    const user = userEvent.setup();
    const calls: string[] = [];
    vi.stubGlobal("fetch", vi.fn(async (url: string) => {
      calls.push(String(url));
      throw new Error("station read");
    }));
    render(<Harness initial={newBranch} />);

    expect(screen.getByRole("heading", { name: "هل تريد الوصول إلى بيانات هذا الفرع من خارج الفرع؟" })).toBeTruthy();
    expect(screen.queryByText("تعذر قراءة نقاط البيع.")).toBeNull();
    expect(calls).toEqual([]);

    await user.click(screen.getByRole("button", { name: "التالي" }));

    expect(screen.getByRole("heading", { name: "نقطة البيع" })).toBeTruthy();
    expect(screen.getByLabelText("اسم نقطة البيع")).toHaveProperty("value", "كاونتر 1");
    expect(screen.getByLabelText("رقم نقطة البيع")).toHaveProperty("value", "1");
    expect(screen.queryByText("تعذر قراءة نقاط البيع.")).toBeNull();
    expect(calls.some((call) => call.includes("stations"))).toBe(false);
  });

  it("keeps the station and device draft through the final setup request", async () => {
    const user = userEvent.setup();
    const submitted: BranchSetupInput[] = [];
    vi.stubGlobal("fetch", vi.fn(async (url: string) => {
      if (String(url).includes("/api/setup/central-admins")) {
        return new Response(JSON.stringify({
          admins: [{
            userId: "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            username: "905017083",
            displayName: "سليم جيوسي",
            fullName: "سليم جيوسي",
            employeeNumber: "905017083",
            roleCode: "Admin",
            roleName: "مدير النظام",
          }],
        }), { status: 200, headers: { "Content-Type": "application/json" } });
      }
      throw new Error("no database yet");
    }));
    render(<Harness initial={{ ...newBranch, stationName: "", stationNumber: "", deviceName: "" }} onSubmit={(value) => submitted.push(value)} />);

    await user.click(screen.getByRole("button", { name: "التالي" }));
    expect(screen.getByLabelText("اسم نقطة البيع")).toHaveProperty("value", "كاونتر 1");
    await user.click(screen.getByRole("button", { name: "التالي" }));
    expect(screen.getByLabelText("اسم الجهاز")).toHaveProperty("value", "صندوق 1");
    await user.click(screen.getByRole("button", { name: "التالي" }));
    expect(screen.getByRole("heading", { name: "مدير الفرع الأولي" })).toBeTruthy();
    expect(screen.queryByLabelText("كلمة المرور")).toBeNull();
    await user.click(screen.getByRole("button", { name: "التالي" }));
    await user.click(screen.getByRole("button", { name: "تنفيذ الإعداد" }));

    expect(submitted).toHaveLength(1);
    expect(submitted[0].stationName).toBe("كاونتر 1");
    expect(submitted[0].stationNumber).toBe("1");
    expect(submitted[0].deviceName).toBe("صندوق 1");
    expect(submitted[0].deviceName).not.toBe(submitted[0].stationName);
  });

  it("loads stations from an existing branch only after the station step", async () => {
    const user = userEvent.setup();
    const calls: string[] = [];
    vi.stubGlobal("fetch", vi.fn(async (url: string) => {
      const href = String(url);
      calls.push(href);
      if (href.includes("/api/setup/branch-stations")) {
        return new Response(JSON.stringify({
          stations: [{ stationId: "11111111-1111-1111-1111-111111111111", stationNumber: 4, stationName: "كاونتر الموجود" }],
        }), { status: 200, headers: { "Content-Type": "application/json" } });
      }
      throw new Error(href);
    }));
    render(<Harness initial={existingBranch} />);

    expect(screen.getByRole("heading", { name: "هل تريد الوصول إلى بيانات هذا الفرع من خارج الفرع؟" })).toBeTruthy();
    expect(screen.queryByText("تعذر قراءة نقاط البيع.")).toBeNull();
    expect(calls).toEqual([]);

    await user.click(screen.getByRole("button", { name: "التالي" }));
    expect(await screen.findByRole("heading", { name: "اختيار نقطة البيع" })).toBeTruthy();
    expect(await screen.findByText("كاونتر الموجود")).toBeTruthy();
    expect(calls.some((call) => call.includes("/api/setup/branch-stations"))).toBe(true);
    expect(calls.some((call) => call.includes("/api/devices/stations"))).toBe(false);
  });
});

describe("create branch request", () => {
  it("sends the drafted station and device when no branch database exists yet", async () => {
    const bodies: string[] = [];
    vi.stubGlobal("fetch", vi.fn(async (_url: string, init?: RequestInit) => {
      bodies.push(String(init?.body ?? ""));
      return new Response(`${JSON.stringify({ type: "completed" })}\n`, {
        status: 200,
        headers: { "Content-Type": "application/x-ndjson" },
      });
    }));

    await createBranch({
      ...newBranch,
      stationNumber: "1",
      stationName: "كاونتر 1",
      deviceName: "صندوق 1",
    }, () => undefined);

    const body = JSON.parse(bodies[0]) as { stationName: string; stationNumber: number; deviceName: string };
    expect(body.stationName).toBe("كاونتر 1");
    expect(body.stationNumber).toBe(1);
    expect(body.deviceName).toBe("صندوق 1");
  });
});
