import { test, expect } from "@playwright/test";
import { emptyResearch } from "./research-fixtures";
test.beforeEach(async ({ page }) => { await page.route("**/api/research/journal", r => r.fulfill({ json: emptyResearch })); });

const portfolio = { investedAmount: 0, unrealizedPercent: 0, currentPortfolioValue: 0, unrealizedPnl: 0, realizedPnl: 0, todayNetPnl: 0, availableCash: 1000, totalCharges: 0, openPositions: 0, activeOrders: 0, totalAccountValue: 1000, reservedCash: 0, valuationStatus: "Synthetic test fixture" };

for (const [time, enabled] of [["01:00:00", false], ["01:30:00", true], ["12:29:00", true], ["12:30:00", false]] as const) {
  test(`Start Trading window ${time} UTC is ${enabled ? "open" : "closed"}`, async ({ page }) => {
    await page.route("**/api/settings", r => r.fulfill({ json: { environment: "Production", tradingMode: "PAPER" } }));
    await page.route("**/api/dashboard", r => r.fulfill({ json: portfolio }));
    await page.route("**/api/system/status", r => r.fulfill({ json: { state: "STOPPED" } }));
    await page.route("**/api/auth/csrf", r => r.fulfill({ json: { token: "test" } }));
    await page.route("**/api/trading/status", r => r.fulfill({ json: { requestedMode: "PAPER", liveReady: false, serverUtc: `2026-11-16T${time}Z` } }));
    await page.goto("/");
    await page.getByRole("navigation").getByRole("button", { name: "Trading", exact: true }).click();
    const start = page.getByRole("button", { name: "Start Trading", exact: true });
    if (enabled) await expect(start).toBeEnabled(); else await expect(start).toBeDisabled();
  });
}

test("mode choice does not start trading; second confirmation is separate and blockers are visible", async ({ page }) => {
  let mode = "PAPER", starts = 0, modeRequests = 0;
  await page.route("**/api/settings", r => r.fulfill({ json: { environment: "Production", tradingMode: "PAPER" } }));
  await page.route("**/api/dashboard", r => r.fulfill({ json: portfolio }));
  await page.route("**/api/system/status", r => r.fulfill({ json: { state: "STOPPED", reason: "Browsing" } }));
  await page.route("**/api/auth/csrf", r => r.fulfill({ json: { token: "test" } }));
  await page.route("**/api/trading/status", r => r.fulfill({ json: { requestedMode: mode, liveReady: false, blocker: "Real-money execution is unavailable. No money committed.", serverUtc: "2026-11-16T04:30:00Z" } }));
  await page.route("**/api/trading/mode", async r => {
    modeRequests++; const body = r.request().postDataJSON();
    expect(body.confirmation).toBe(body.mode === "LIVE" ? "GO LIVE" : "PAPER");
    mode = body.mode; await r.fulfill({ json: { requestedMode: mode } });
  });
  await page.route("**/api/trading/start", async r => {
    starts++; expect(r.request().postDataJSON()).toEqual({ confirmation: "START TRADING" });
    await r.fulfill({ status: 400, json: { error: "Real-money execution is unavailable. No money committed." } });
  });
  await page.goto("/");
  await page.getByRole("button", { name: "Go LIVE", exact: true }).click();
  await page.getByLabel("Type GO LIVE exactly").fill("GO LIVE");
  await page.getByRole("button", { name: "Confirm GO LIVE" }).click();
  await expect(page.getByRole("button", { name: "Use PAPER" })).toBeVisible();
  expect(modeRequests).toBe(1); expect(starts).toBe(0);
  await page.getByRole("navigation").getByRole("button", { name: "Trading", exact: true }).click();
  await page.getByRole("button", { name: "Start Trading", exact: true }).click();
  await page.getByLabel("Type START TRADING exactly").fill("START TRADING ");
  await expect(page.getByRole("button", { name: "Confirm START TRADING" })).toBeDisabled();
  await page.getByLabel("Type START TRADING exactly").fill("START TRADING");
  await page.getByRole("button", { name: "Confirm START TRADING" }).click();
  await expect(page.getByRole("alert")).toContainText("No money committed");
  expect(starts).toBe(1);
  await page.screenshot({ path: "test-results/trading-confirmation.png", fullPage: true });
  await page.getByRole("button", { name: "Cancel", exact: true }).click();
  await page.getByRole("button", { name: "Use PAPER" }).click();
  await page.getByLabel("Type PAPER exactly").fill("PAPER");
  await page.getByRole("button", { name: "Confirm PAPER" }).click();
  await expect(page.getByRole("button", { name: "Go LIVE", exact: true })).toBeVisible();
  expect(starts).toBe(1);
});

test("gainers are fetched explicitly and partial coverage and failures are labelled", async ({ page }) => {
  let scans = 0;
  await page.route("**/api/settings", r => r.fulfill({ json: { environment: "Production", tradingMode: "PAPER" } }));
  await page.route("**/api/dashboard", r => r.fulfill({ json: portfolio }));
  await page.route("**/api/system/status", r => r.fulfill({ json: { state: "STOPPED" } }));
  await page.route("**/api/auth/csrf", r => r.fulfill({ json: { token: "test" } }));
  await page.route("**/api/trading/status", r => r.fulfill({ json: { requestedMode: "PAPER", liveReady: false, serverUtc: "2026-11-16T04:30:00Z" } }));
  await page.route("**/api/market/top-gainers", r => {
    scans++;
    return scans === 1 ? r.fulfill({ json: { items: [{ symbol: "TEST_FIXTURE", exchange: "NSE", code: "NSE_1", price: 110, changePercent: 10, volume: 50 }], universeCount: 100, quotedCount: 80, scanStartedUtc: "2026-11-16T04:30:00Z", fetchedUtc: "2026-11-16T04:31:00Z", scope: "Test-only mock universe", freshness: "Quote trading date is not supplied" } }) : r.fulfill({ status: 400, json: { error: "Broker authentication failed" } });
  });
  await page.goto("/");
  await expect(page.getByRole("button", { name: "Refresh gainers" })).toBeVisible(); expect(scans).toBe(0);
  await page.getByRole("button", { name: "Refresh gainers" }).click();
  await expect(page.getByRole("cell", { name: "TEST_FIXTURE", exact: true })).toBeVisible();
  await expect(page.getByText("Partial quote coverage:", { exact: false })).toBeVisible();
  await page.getByRole("button", { name: "Refresh gainers" }).click();
  await expect(page.getByRole("alert")).toContainText("Broker authentication failed");
  await expect(page.getByRole("cell", { name: "TEST_FIXTURE", exact: true })).toHaveCount(0);
});
