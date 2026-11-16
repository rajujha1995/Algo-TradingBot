export type Portfolio = {
  investedAmount: number;
  currentPortfolioValue: number;
  unrealizedPnl: number;
  unrealizedPercent: number;
  realizedPnl: number;
  todayNetPnl: number;
  availableCash: number;
  totalCharges: number;
  openPositions: number;
  activeOrders: number;
  totalAccountValue: number;
  reservedCash: number;
  valuationStatus: string;
};
export type Settings = {
  environment: string;
  tradingMode: string;
  capitalLimit: number;
  maxOpenPositions: number;
  dailyLossLimit: number;
  totalLossLimit: number;
  brokerTokenConfigured: boolean;
};
export type Control = { state: string; reason: string; updatedUtc: string };
export const money = (n: number) =>
  new Intl.NumberFormat("en-IN", {
    style: "currency",
    currency: "INR",
    maximumFractionDigits: 2,
  }).format(n);
export const time = (s: string) =>
  new Date(s.endsWith("Z") ? s : s + "Z").toLocaleString("en-IN", {
    timeZone: "Asia/Kolkata",
  });
