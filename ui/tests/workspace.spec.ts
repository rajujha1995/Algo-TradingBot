import { test, expect } from "@playwright/test";
test("authenticate, recover paper, submit simulation and persist emergency stop", async ({
  page,
}) => {
  const password = process.env.E2E_PASSWORD;
  if (!password)
    throw new Error(
      "Set E2E_PASSWORD to the local operator password. Use a disposable test database.",
    );
  await page.goto("/");
  await page.getByLabel("Operator password").fill(password);
  await page.getByRole("button", { name: "Open workspace" }).click();
  await expect(
    page.getByRole("heading", { name: "Your portfolio, at a glance." }),
  ).toBeVisible();
  await page.getByRole("button", { name: "EMERGENCY STOP" }).click();
  await expect(page.getByText("Emergency stop is active.")).toBeVisible();
  await page.getByRole("navigation").getByRole("button", { name: "Trading", exact: true }).click();
  const tradingStatus = await (await page.request.get("/api/trading/status")).json();
  test.skip(!tradingStatus.startWindowOpen, "Paper session starts only within 07:00–18:00 IST");
  // Legacy DEMO ledger remains independently tested; Start Trading now starts research.
  const csrf = await (await page.request.get("/api/auth/csrf")).json();
  expect((await page.request.post("/api/system/recover", { data: { confirmation: "START PAPER" }, headers: { "X-CSRF-TOKEN": csrf.token } })).status()).toBe(200);
  await expect(page.getByText("PAPER_RUNNING", { exact: true })).toBeVisible();
  await page
    .getByRole("navigation")
    .getByRole("button", { name: "Orders", exact: true })
    .click();
  await page.getByLabel("Quantity", { exact: true }).fill("2");
  await page.getByLabel("Simulated price (₹)").fill("100");
  await page.getByRole("button", { name: "Submit paper order" }).click();
  await expect(
    page.getByRole("cell", { name: "FILLED", exact: true }).first(),
  ).toBeVisible();
  await page.getByRole("button", { name: "EMERGENCY STOP" }).click();
  await page.reload();
  await expect(page.getByText("Emergency stop is active.")).toBeVisible();
});

test("API requires operator auth and CSRF and never returns credentials", async ({
  request,
}) => {
  expect((await request.get("/api/dashboard")).status()).toBe(401);
  expect(
    (
      await request.post("/api/auth/login", {
        data: { password: "irrelevant" },
      })
    ).status(),
  ).toBe(403);
  const csrf = await (await request.get("/api/auth/csrf")).json();
  const login = await request.post("/api/auth/login", {
    data: { password: process.env.E2E_PASSWORD },
    headers: { "X-CSRF-TOKEN": csrf.token },
  });
  expect(login.status()).toBe(200);
  const response = await request.get("/api/settings");
  expect(response.status()).toBe(200);
  const settings = await response.json();
  expect(settings.tradingMode).toBe("PAPER");
  expect(settings.autoTradingEnabled).toBe(false);
  expect(JSON.stringify(settings)).not.toContain(process.env.E2E_PASSWORD!);
  expect(settings).not.toHaveProperty("accessToken");
  expect(
    (
      await request.post("/api/system/recover", {
        data: { confirmation: "START PAPER" },
      })
    ).status(),
  ).toBe(403);
});

test("backtest accepts the documented synthetic candle file and renders results", async ({
  page,
}) => {
  await page.goto("/");
  await page.getByLabel("Operator password").fill(process.env.E2E_PASSWORD!);
  await page.getByRole("button", { name: "Open workspace" }).click();
  await page
    .getByRole("navigation")
    .getByRole("button", { name: "Backtesting", exact: true })
    .click();
  await page
    .getByLabel("Historical candle JSON")
    .setInputFiles("../docs/examples/backtest-demo.json");
  await page.getByRole("button", { name: "Run backtest" }).click();
  await expect(
    page.getByRole("heading", { name: "Research results" }),
  ).toBeVisible();
  await expect(page.getByText("numberOfTrades", { exact: true })).toBeVisible();
});
