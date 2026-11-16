export const fundsFields = [
  ["eq_cnc", "Available for equity delivery"],
  ["sod_balance", "Start-of-day balance"],
  ["withdrawal_balance", "Withdrawable balance"],
  ["funds_added", "Funds added"],
  ["funds_withdrawn", "Funds withdrawn"],
  ["realized_pnl", "Broker realized P&L"],
  ["unrealized_pnl", "Broker unrealized P&L"],
  ["brokerage", "Brokerage"],
  ["eq_charges", "Equity charges"],
] as const;
export function parseFunds(response: unknown): Record<string, number | null> {
  const r = response as { status?: string; data?: Record<string, unknown> } | null;
  if (r?.status !== "success" || !r.data || typeof r.data !== "object" || Array.isArray(r.data))
    throw new Error("Broker funds response was unsuccessful or invalid. Try refreshing after checking the token.");
  const data = r.data;
  const delivery = data.detailed_avl_balance as Record<string, unknown> | null;
  return Object.fromEntries(fundsFields.map(([key]) => {
    const value = key === "eq_cnc" ? delivery?.eq_cnc : data[key];
    return [key, typeof value === "number" && Number.isFinite(value) ? value : null];
  }));
}
