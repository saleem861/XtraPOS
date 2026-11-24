import { describe, expect, it } from "vitest";
import { distinctBy } from "./distinct";

// قاعدة المالك: لا يتكرر اسم في أي قائمة منسدلة أو قائمة اختيار في كل التطبيق.
describe("distinctBy", () => {
  it("keeps the first of each displayed name, ignoring case and spaces", () => {
    const users = [
      { id: "1", name: "سليم" },
      { id: "2", name: " سليم " },
      { id: "3", name: "Ahmad" },
      { id: "4", name: "ahmad" },
      { id: "5", name: "رنا" },
    ];
    expect(distinctBy(users, (user) => user.name).map((user) => user.id)).toEqual(["1", "3", "5"]);
  });

  it("drops empty names and accepts a missing list", () => {
    expect(distinctBy([{ name: "" }, { name: "  " }, { name: "فرع" }], (row) => row.name)).toEqual([{ name: "فرع" }]);
    expect(distinctBy(undefined, (row: { name: string }) => row.name)).toEqual([]);
  });
});
