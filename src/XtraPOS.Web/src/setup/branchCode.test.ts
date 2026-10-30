import { describe, expect, it } from "vitest";
import { emptyBranchSetup } from "../types/setup";
import {
  BRANCH_CODE_DUPLICATE_MESSAGE,
  branchCodeRequest,
  isDuplicateBranchCode,
  suggestBranchCode,
} from "./branchCode";

describe("branch code suggestion", () => {
  it("suggests 01 when the business has no numeric code", () => {
    expect(suggestBranchCode([])).toBe("01");
    expect(suggestBranchCode([null, "المستودع"])).toBe("01");
  });

  it("suggests the next padded code and fills gaps", () => {
    expect(suggestBranchCode(["01", "02", "03"])).toBe("04");
    expect(suggestBranchCode(["01", "02", "04"])).toBe("03");
  });

  it("rejects a duplicate manual code and accepts a different one", () => {
    expect(isDuplicateBranchCode(["01", "02", "03"], "02")).toBe(true);
    expect(isDuplicateBranchCode(["01", "02", "03"], "09")).toBe(false);
    expect(isDuplicateBranchCode(["01", "02", "03"], "01", "01")).toBe(false);
    expect(isDuplicateBranchCode(["01", "01"], "01", "01")).toBe(true);
    expect(BRANCH_CODE_DUPLICATE_MESSAGE).toBe("رمز الفرع مستخدم مسبقًا. اختر رمزًا آخر.");
  });

  it("asks Central for a linked branch and the local branch database for standalone", () => {
    const central = branchCodeRequest({
      ...emptyBranchSetup,
      operatingMode: "CentralManagement",
      centralIntent: "existing",
      centralDatabaseName: "XtraPOS_Central",
      businessName: "SmokerWorld",
    });
    expect(central.source).toBe("Central");
    expect(central.databaseName).toBe("XtraPOS_Central");
    expect(central.businessName).toBe("SmokerWorld");

    const standalone = branchCodeRequest({
      ...emptyBranchSetup,
      operatingMode: "Standalone",
      branchIntent: "new",
    }, "09");
    expect(standalone.source).toBe("Branch");
    expect(standalone.intent).toBe("new");
    expect(standalone.branchCode).toBe("09");
  });
});
