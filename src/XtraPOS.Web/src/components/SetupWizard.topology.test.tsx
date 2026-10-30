import { useState } from "react";
import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it } from "vitest";
import { emptyBranchSetup } from "../types/setup";
import { stepCounterLabel, visibleSteps } from "../setup/wizardFlow";
import { acceptPendingSales, createPendingSale, OFFLINE_CACHE_STORE, OFFLINE_STOCK_NOTICE } from "../setup/offlineContract";
import { SetupWizard } from "./SetupWizard";

afterEach(() => cleanup());

describe("setup wizard", () => {
  it("starts with one device-role decision and a disabled previous button", () => {
    render(
      <SetupWizard
        value={emptyBranchSetup}
        onChange={() => undefined}
        onCancel={() => undefined}
        onSubmit={() => undefined}
      />,
    );

    expect(screen.getByRole("heading", { name: "إعداد XtraPOS" })).toBeTruthy();
    expect(screen.getByText("الخطوة 1")).toBeTruthy();
    expect(screen.queryByText(/من \d/)).toBeNull();
    expect(stepCounterLabel(0, 5, false)).toBe("الخطوة 1");
    expect(stepCounterLabel(3, 6, true)).toBe("الخطوة 4 من 6");
    expect(stepCounterLabel(5, 6, true)).toBe("الخطوة 6 من 6");
    expect(stepCounterLabel(2, 3, false)).toBe("الخطوة 3");
    expect(screen.getByText("خادم / جهاز رئيسي لفرع")).toBeTruthy();
    expect(screen.getByText("جهاز إضافي داخل فرع موجود")).toBeTruthy();
    expect(screen.getByText("خادم إدارة مركزية فقط")).toBeTruthy();
    expect(screen.getByRole("button", { name: "السابق" })).toHaveProperty("disabled", true);
    expect(screen.getByRole("button", { name: "التالي" })).toHaveProperty("disabled", true);
  });

  it("skips the second device-type screen after the welcome choice", () => {
    const steps = visibleSteps({ ...emptyBranchSetup, deviceRole: "MainBranch", roleLocked: true, operatingMode: "Standalone", branchIntent: "new", branchLocation: "Local" });
    expect(steps[0]).not.toBe("deviceRole");
    expect(steps).not.toContain("deviceRole");
    render(
      <SetupWizard
        value={{ ...emptyBranchSetup, deviceRole: "MainBranch", roleLocked: true, operatingMode: "Standalone" }}
        onChange={() => undefined}
        onCancel={() => undefined}
        onSubmit={() => undefined}
      />,
    );
    expect(screen.queryByText("خادم / جهاز رئيسي لفرع")).toBeNull();
  });

  it("keeps central, branch, and secondary paths on separate steps", () => {
    const central = visibleSteps({ ...emptyBranchSetup, deviceRole: "CentralOnly", centralIntent: "new", centralLocation: "Remote" });
    expect(central).toEqual(["deviceRole", "centralIntent", "centralPlace", "centralConnection", "centralBusiness", "centralReview"]);
    expect(central).not.toContain("branchIdentity");

    const linked = visibleSteps({
      ...emptyBranchSetup,
      deviceRole: "MainBranch",
      operatingMode: "CentralManagement",
      centralIntent: "existing",
      branchIntent: "existing",
      branchLocation: "Local",
      branchRemoteChoice: "yes",
    });
    expect(linked).not.toContain("businessIdentity");
    expect(linked).toContain("branchDiscover");
    expect(linked).not.toContain("branchConnection");
    expect(linked).toContain("remoteConnection");

    const standalone = visibleSteps({
      ...emptyBranchSetup,
      deviceRole: "MainBranch",
      operatingMode: "Standalone",
      branchIntent: "new",
      branchLocation: "Local",
      branchRemoteChoice: "no",
    });
    expect(standalone).toContain("businessIdentity");
    expect(standalone).not.toContain("centralIntent");
    expect(standalone).toContain("branchConnection");

    const secondary = visibleSteps(
      { ...emptyBranchSetup, deviceRole: "Secondary" },
      [
        { branchId: "a", branchName: "المحل", databaseName: "XtraPOS_Branch" },
        { branchId: "b", branchName: "المستودع", databaseName: "XtraPOS_Branch" },
      ],
    );
    expect(secondary).toEqual([
      "deviceRole",
      "secondaryConnect",
      "branchSelect",
      "deviceAuthorization",
      "posStation",
      "deviceIdentity",
      "offlinePrep",
      "secondaryReview",
    ]);
    expect(visibleSteps({ ...emptyBranchSetup, deviceRole: "Secondary" }, [{ branchId: "a", branchName: "المحل", databaseName: "XtraPOS_Branch" }])).toEqual([
      "deviceRole",
      "secondaryConnect",
      "deviceAuthorization",
      "posStation",
      "deviceIdentity",
      "offlinePrep",
      "secondaryReview",
    ]);
  });

  it("moves to the next screen only after a choice", async () => {
    const user = userEvent.setup();
    function Harness() {
      const [value, setValue] = useState(emptyBranchSetup);
      return <SetupWizard value={value} onChange={setValue} onCancel={() => undefined} onSubmit={() => undefined} />;
    }
    render(<Harness />);

    expect(screen.getByRole("button", { name: "التالي" })).toHaveProperty("disabled", true);
    await user.click(screen.getByText("خادم إدارة مركزية فقط"));
    await user.click(screen.getByRole("button", { name: "التالي" }));
    expect(screen.getByRole("heading", { name: "إعداد الإدارة المركزية" })).toBeTruthy();
    await user.click(screen.getByRole("button", { name: "السابق" }));
    expect(screen.getByText("جهاز إضافي داخل فرع موجود")).toBeTruthy();
  });
});

describe("offline sale contract", () => {
  it("keeps a pending sale idempotent and out of localStorage", () => {
    const sale = createPendingSale("device-1", "station-1", "2026-10-07T00:00:00Z", "sale-1");
    const again = createPendingSale("device-1", "station-1", "2026-10-07T00:00:00Z", "sale-1");
    const stored = acceptPendingSales([sale], [again]);
    expect(stored).toHaveLength(1);
    expect(sale.offlineCreated).toBe(true);
    expect(OFFLINE_CACHE_STORE).toBe("IndexedDB");
    expect(OFFLINE_STOCK_NOTICE).toContain("غير متصل");
    expect(JSON.stringify(sale)).not.toMatch(/password|connectionString/i);
  });
});
