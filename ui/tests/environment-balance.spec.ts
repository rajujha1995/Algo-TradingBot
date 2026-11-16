import { test, expect } from "@playwright/test";

test("balance refresh distinguishes missing values and clears failed snapshots; Go LIVE requires exact text", async ({ page }) => {
  await page.route("**/api/settings", route => route.fulfill({ json: { environment: "Development", tradingMode: "PAPER", brokerTokenConfigured: true } }));
  await page.route("**/api/auth/csrf", route => route.fulfill({ json: { token: "test-only" } }));
  await page.route("**/api/dashboard", route => route.fulfill({ json: { investedAmount: 0, unrealizedPercent: 0, currentPortfolioValue: 0, unrealizedPnl: 0, realizedPnl: 0, todayNetPnl: 0, availableCash: 1000, totalCharges: 0, openPositions: 0, activeOrders: 0, totalAccountValue: 1000, reservedCash: 0, valuationStatus: "Synthetic test fixture" } }));
  await page.route("**/api/system/status", route => route.fulfill({ json: { state: "STOPPED", reason: "Test" } }));
  await page.route("**/api/trading/status", route => route.fulfill({ json: { requestedMode: "PAPER", liveReady: false, blocker: "Live unavailable", serverUtc: "2026-11-16T04:30:00Z", deployment: "Production" } }));
  let calls = 0;
  await page.route("**/api/broker/funds", route => {
    calls++;
    return calls === 1 ? route.fulfill({ json: { status: "success", data: { detailed_avl_balance: { eq_cnc: 123 }, brokerage: 0 } } })
      : route.fulfill({ status: 400, json: { error: "Broker authentication failed." } });
  });
  await page.route("**/api/trading/mode", async route => {
    expect(route.request().postDataJSON()).toEqual({ mode: "LIVE", confirmation: "GO LIVE" });
    await route.fulfill({ status: 400, json: { error: "Stop the current session before changing mode." } });
  });
  await page.goto("/");
  await expect(page.getByText("Balance not fetched yet.", { exact: false })).toBeVisible();
  expect(calls).toBe(0);
  await page.getByRole("button", { name: "Refresh broker balance" }).click();
  await expect(page.getByText("₹123.00", { exact: true })).toBeVisible();
  await expect(page.getByText("Not provided", { exact: true }).first()).toBeVisible();
  await page.getByRole("button", { name: "Refresh broker balance" }).click();
  await expect(page.getByText("Broker authentication failed.", { exact: true })).toBeVisible();
  await expect(page.getByText("₹123.00", { exact: true })).toHaveCount(0);
  await page.getByRole("button", { name: "Go LIVE", exact: true }).click();
  await page.getByLabel("Type GO LIVE exactly").fill("go live");
  await expect(page.getByRole("button", { name: "Confirm GO LIVE" })).toBeDisabled();
  await page.getByLabel("Type GO LIVE exactly").fill("GO LIVE");
  await page.screenshot({ path: "test-results/go-live-confirmation.png", fullPage: true });
  await page.getByRole("button", { name: "Confirm GO LIVE" }).click();
  await expect(page.getByRole("alert").filter({ hasText: "Stop the current session" })).toBeVisible();
  await page.getByRole("button", { name: "Cancel", exact: true }).click();
  await expect(page.getByRole("dialog")).toHaveCount(0);
});
