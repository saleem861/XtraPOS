import { describe, expect, it } from "vitest";
import { decideBranchAgent, localPosAfterSetup, type LocalAgentStatus } from "./branchAgentLocal";

const ready: LocalAgentStatus = {
  product: "XtraPOS.BranchAgent",
  version: "0.3.0",
  machineName: "BRANCH-PC",
  paired: true,
  server: "https://test-pos.sharbelfashion.com",
  agentId: "6f0c0c5a-3d2e-4b8a-9c11-1234567890ab",
  instances: ["localhost", "localhost\\BARTENDER"],
  sqlAvailable: true,
};

describe("branch agent detection", () => {
  it("asks for the installer when the local service is absent", () => {
    expect(decideBranchAgent(null, ready.server, "0.3.0")).toBe("missing");
  });

  it("continues when the paired agent matches this site and reports SQL", () => {
    expect(decideBranchAgent(ready, ready.server + "/", "0.3.0")).toBe("ready");
  });

  it("does not treat another site as this environment", () => {
    expect(decideBranchAgent({ ...ready, server: "https://other.example" }, ready.server, "0.3.0")).toBe("other-environment");
  });

  it("pairs a running agent before provisioning", () => {
    expect(decideBranchAgent({ ...ready, paired: false, agentId: null }, ready.server, "0.3.0")).toBe("unpaired");
  });

  it("reports a missing SQL Server and an older agent", () => {
    expect(decideBranchAgent({ ...ready, sqlAvailable: false }, ready.server, "0.3.0")).toBe("sql-missing");
    expect(decideBranchAgent({ ...ready, version: "0.2.0" }, ready.server, "0.3.0")).toBe("outdated");
  });

  it("opens the local application after setup when the browser is still on the published site", () => {
    expect(localPosAfterSetup("http://127.0.0.1:47335", "https://test-pos.sharbelfashion.com/")).toBe("http://127.0.0.1:47335");
    expect(localPosAfterSetup("http://127.0.0.1:47335", "http://127.0.0.1:47335/")).toBeNull();
  });
});
