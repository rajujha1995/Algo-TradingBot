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
  it("preserves explicit UTC and India offsets", () => {
    expect(time("2026-01-01T00:00:00+00:00")).toBe(time("2026-01-01T00:00:00Z"));
    expect(time("2026-01-01T05:30:00+05:30")).toBe(time("2026-01-01T00:00:00Z"));
  });
});
