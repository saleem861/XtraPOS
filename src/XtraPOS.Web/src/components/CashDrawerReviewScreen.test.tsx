import { describe, expect, it } from "vitest";
import { reviewChoices } from "./CashDrawerReviewScreen";

describe("cash drawer review choices", () => {
  it("lists each branch name and each point once", () => {
    const choices = reviewChoices({
      branches: [
        { id: "b1", name: "شمس" },
        { id: "b2", name: "شمس" },
        { id: "b3", name: "قمر" },
      ],
      stations: [
        { id: "s1", branchId: "b1", stationNumber: 1, name: "كاونتر" },
        { id: "s2", branchId: "b2", stationNumber: 1, name: "كاونتر" },
        { id: "s3", branchId: "b1", stationNumber: 2, name: "كاونتر" },
      ],
      users: [],
    }, "");

    expect(choices.branches.map((branch) => branch.name)).toEqual(["شمس", "قمر"]);
    expect(choices.stations.map((station) => `${station.stationNumber}-${station.name}`)).toEqual(["1-كاونتر", "2-كاونتر"]);
  });
});
