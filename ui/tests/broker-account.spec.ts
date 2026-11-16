import { workspace } from "./realtime-fixtures";
import { test, expect } from "@playwright/test";

test("account monitoring is read-only, shows streamed marks separately, and hides failed/stale prices", async ({ page }) => {
  let enabled = false, failed = false, toggles = 0;
  const now = () => new Date().toISOString();
  function snapshot() {
    return { enabled, serverUtc: now(), priceFeed: enabled ? "Receiving fixture prices" : "Waiting", mappingStatus: "Resolved fixture instrument", policy: "Read-only test fixture; no broker orders", sections: enabled ? {
      funds: { data: { status: "success", data: { detailed_avl_balance: { eq_cnc: 500 }, brokerage: 0 } }, fetchedUtc: now(), error: null },
      holdings: { data: { status: "success", data: [{ symbol: "TEST_HOLDING", total_qty: 10, avg_price: 100 }] }, fetchedUtc: now(), error: null },
      positions: { data: { status: "success", data: [] }, fetchedUtc: now(), error: null },
      orders: { data: { status: "success", data: [{ id: "FIXTURE_1", name: "TEST_HOLDING", txn_type: "BUY", status: "PARTIALLY FILLED", requested_qty: 10, traded_qty: 2 }] }, fetchedUtc: now(), error: null },
      trades: { data: { status: "success", data: [] }, fetchedUtc: now(), error: null },
    } : {}, holdingMarks: enabled ? [{ symbol: "TEST_HOLDING", code: "NSE_1", quantity: 10, averagePrice: 100, price: 110.11, priceUtc: now(), marketValue: 1101.1, unrealizedPnl: 101.1, fresh: true, note: "Fixture mark" }] : [], positionMarks: [], watchlist: enabled ? [{ symbol: "TEST_HOLDING", code: "NSE_1", price: 110.11, priceUtc: now(), fresh: true }] : [], orderFeed: { connected: enabled, reconciled: enabled, status: enabled ? "Receiving fixture order events" : "Stopped", limitation: "Lots are not share quantities", events: enabled ? [{ orderId: "FIXTURE_1", publishedUtc: now(), receivedUtc: now(), status: "Partially filled", name: "TEST_HOLDING", side: "BUY", requestedQuantity: 10, filledLots: null, executionPrice: null }] : [] } };
  }
  await page.route("**/api/settings", r => r.fulfill({ json: { environment: "Production", workspace: "REALTIME", brokerTokenConfigured: true } }));
  await page.route("**/api/auth/csrf", r => r.fulfill({ json: { token: "fixture" } }));
  await page.route("**/api/broker/account", r => failed ? r.fulfill({ status: 503, json: { error: "Fixture connection lost" } }) : r.fulfill({ json: snapshot() }));
  await page.route("**/api/broker/account/monitor", r => { toggles++; enabled = r.request().postDataJSON().enabled; return r.fulfill({ json: snapshot() }); });
  await page.route("**/api/workspace", r => r.fulfill({json: workspace()}));
  await page.route("**/api/market/top-gainers", r => r.fulfill({status:503,json:{error:"Unavailable fixture"}}));
  await page.goto("/");
  await page.getByRole("navigation").getByRole("button", { name: "Broker Account", exact: true }).click();
  await expect(page.getByRole("button", { name: "Start account monitoring", exact: true })).toBeEnabled();
  expect(toggles).toBe(0);
  await page.getByRole("button", { name: "Start account monitoring", exact: true }).click();
  await expect(page.getByText("Monitoring enabled", { exact: true })).toBeVisible();
  await expect(page.getByText("₹110.11", { exact: true }).first()).toBeVisible();
  await expect(page.getByText("₹500.00", { exact: true })).toBeVisible();
  await expect(page.getByRole("cell", { name: "Partially filled", exact: true })).toBeVisible();
  await expect(page.getByText("Not available", { exact: true }).first()).toBeVisible();
  await expect(page.getByRole("button", { name: "Start Trading", exact: true })).toHaveCount(0);
  await page.screenshot({ path: "test-results/broker-account.png", fullPage: true });
  failed = true;
  await page.evaluate(() => window.dispatchEvent(new Event("market-refresh")));
  await expect(page.getByRole("alert")).toContainText("Fixture connection lost");
  await expect(page.getByText("₹110.11", { exact: true })).toHaveCount(0);
  await expect(page.getByText("No fresh price", { exact: true })).toBeVisible();
  failed = false;
  await page.getByRole("button", { name: "Stop account monitoring", exact: true }).click();
  await expect(page.getByText("Monitoring stopped", { exact: true })).toBeVisible(); expect(toggles).toBe(2);
});

test("cached account endpoints require auth and CSRF without making broker requests", async ({ request }) => {
  test.skip(!process.env.E2E_ISOLATED, "Requires a disposable PROD API host");
  expect((await request.get("/api/broker/account")).status()).toBe(401);
  expect((await request.post("/api/broker/account/monitor", { data: { enabled: false } })).status()).toBe(401);
  const csrf = await (await request.get("/api/auth/csrf")).json();
  expect((await request.post("/api/auth/login", { data: { password: process.env.E2E_PASSWORD }, headers: { "X-CSRF-TOKEN": csrf.token } })).status()).toBe(200);
  expect((await request.post("/api/broker/account/monitor", { data: { enabled: false } })).status()).toBe(403);
  const fresh = await (await request.get("/api/auth/csrf")).json();
  expect((await request.post("/api/broker/account/monitor", { data: { enabled: false }, headers: { "X-CSRF-TOKEN": fresh.token } })).status()).toBe(200);
  // Cached local reads must not exhaust the outbound broker request limiter.
  for (let i = 0; i < 20; i++) expect((await request.get("/api/broker/account")).status()).toBe(200);
});
