import { describe, it, expect } from "vitest";
import { parseFunds } from "./brokerFunds";
describe("broker funds", () => {
  it("keeps delivery availability distinct from start-of-day balance", () => {
    expect(parseFunds({ status: "success", data: { sod_balance: 100, detailed_avl_balance: { eq_cnc: 40 }, brokerage: 0, realized_pnl: -5 } }))
      .toMatchObject({ eq_cnc: 40, sod_balance: 100, brokerage: 0, realized_pnl: -5, withdrawal_balance: null });
  });
  it("does not invent zero for missing or invalid fields", () => {
    expect(parseFunds({ status: "success", data: { sod_balance: "100", brokerage: Infinity, detailed_avl_balance: null } }))
      .toMatchObject({ sod_balance: null, brokerage: null, eq_cnc: null });
  });
  it.each([null, {}, { status: "error", data: {} }, { status: "success", data: [] }])("rejects unsuccessful or malformed responses", value => {
    expect(() => parseFunds(value)).toThrow();
  });
});
