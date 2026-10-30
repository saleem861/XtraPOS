import { describe, expect, it } from "vitest";
import { emptyBranchSetup } from "../types/setup";
import {
  branchDatabaseNameReady,
  connectionContextKey,
  connectionInputsReady,
  connectionVerified,
  displayedServer,
  suggestNewBranchDatabaseName,
  withConnectionVerified,
} from "./connectionContext";

describe("connection context", () => {
  it("requires a remote host and database, and SQL credentials only for SQL authentication", () => {
    expect(connectionInputsReady({
      remote: true,
      host: "",
      databaseName: "XtraPOS_Branch",
      authentication: "Integrated",
      username: "",
      password: "",
    })).toBe(false);
    expect(connectionInputsReady({
      remote: true,
      host: "qa-branch",
      databaseName: "XtraPOS_Branch",
      authentication: "Integrated",
      username: "",
      password: "",
    })).toBe(true);
    expect(connectionInputsReady({
      remote: true,
      host: "qa-branch",
      databaseName: "XtraPOS_Branch",
      authentication: "Sql",
      username: "",
      password: "",
    })).toBe(false);
    expect(connectionInputsReady({
      remote: true,
      host: "qa-branch",
      databaseName: "XtraPOS_Branch",
      authentication: "Sql",
      username: "sa",
      password: "secret",
    })).toBe(true);
  });

  it("does not turn a missing remote host into localhost", () => {
    expect(displayedServer("Remote", "")).toBe("");
    expect(displayedServer("Remote", "localhost")).toBe("localhost");
    expect(displayedServer("Local", "")).toBe("localhost");
  });

  it("suggests a branch database name that is not the central catalog", () => {
    expect(suggestNewBranchDatabaseName("04")).toBe("XtraPOS_Branch_04");
    expect(suggestNewBranchDatabaseName("04")).not.toBe("XtraPOS_Central");
    expect(branchDatabaseNameReady("XtraPOS_Central")).toBe(false);
    expect(branchDatabaseNameReady("XtraPOS_Branch_04")).toBe(true);
    expect(branchDatabaseNameReady("XtraPOS_Branch", ["XtraPOS_Branch"])).toBe(false);
  });

  it("invalidates verification when the branch host changes", () => {
    const verified = withConnectionVerified({
      ...emptyBranchSetup,
      deviceRole: "MainBranch",
      operatingMode: "Standalone",
      branchIntent: "new",
      branchLocation: "Remote",
      branchServer: "qa-branch",
      branchDatabaseName: "XtraPOS_Branch",
    }, "branch");
    expect(connectionVerified(verified, "branch")).toBe(true);
    expect(connectionVerified({ ...verified, branchServer: "other-host" }, "branch")).toBe(false);
    expect(connectionContextKey(verified, "branch")).not.toBe(connectionContextKey({
      ...verified,
      centralLocation: "Remote",
      centralServer: "qa-central",
    }, "branch"));
  });
});
