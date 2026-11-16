import { describe, it, expect } from "vitest";
import { money, time } from "./index";
describe("presentation", () => {
  it("formats INR without hiding losses", () => {
    expect(money(-25)).toContain("25");
    expect(money(-25)).toContain("-");
  });
  it("interprets persisted UTC timestamps as IST", () => {
    expect(time("2026-01-01T00:00:00")).toContain("5:30");
  });
});
