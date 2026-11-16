import { test, expect } from "@playwright/test";
test("mode and start endpoints enforce auth, CSRF, confirmations and live blockers", async ({ request }) => {
  test.skip(!process.env.E2E_ISOLATED, "Requires a separately launched disposable PROD database");
  const mode = "/api/trading/mode";
  expect((await request.post(mode, { data: { mode: "LIVE", confirmation: "GO LIVE" } })).status()).toBe(401);
  let csrf = (await (await request.get("/api/auth/csrf")).json()).token;
  expect((await request.post("/api/auth/login", { data: { password: process.env.E2E_PASSWORD }, headers: { "X-CSRF-TOKEN": csrf } })).status()).toBe(200);
  expect((await request.post(mode, { data: { mode: "LIVE", confirmation: "GO LIVE" } })).status()).toBe(403);
  csrf = (await (await request.get("/api/auth/csrf")).json()).token;
  const headers = { "X-CSRF-TOKEN": csrf };
  expect((await request.post(mode, { data: { mode: "LIVE", confirmation: "go live" }, headers })).status()).toBe(400);
  expect((await request.post(mode, { data: { mode: "LIVE", confirmation: "GO LIVE" }, headers })).status()).toBe(200);
  headers["X-CSRF-TOKEN"] = (await (await request.get("/api/auth/csrf")).json()).token;
  const status = await (await request.get("/api/trading/status")).json();
  expect(status.requestedMode).toBe("LIVE"); expect(status.liveReady).toBe(false);
  const before = await (await request.get("/api/system/status")).json();
  expect(before.state).not.toContain("RUNNING");
  expect((await request.post("/api/trading/start", { data: { confirmation: "START TRADING" } })).status()).toBe(403);
  expect((await request.post("/api/trading/start", { data: { confirmation: "START TRADING" }, headers })).status()).toBe(400);
  expect((await request.post("/api/trading/start", { data: { confirmation: "start trading" }, headers })).status()).toBe(400);
  expect((await (await request.get("/api/system/status")).json()).state).toBe(before.state);
  expect((await request.post(mode, { data: { mode: "PAPER", confirmation: "PAPER" }, headers })).status()).toBe(200);
  headers["X-CSRF-TOKEN"] = (await (await request.get("/api/auth/csrf")).json()).token;
  expect((await (await request.get("/api/trading/status")).json()).requestedMode).toBe("PAPER");
  if (!status.startWindowOpen) {
    const outside = await request.post("/api/trading/start", { data: { confirmation: "START TRADING" }, headers });
    expect(outside.status()).toBe(400); expect((await outside.json()).error).toContain("7:00 AM");
    expect((await request.post("/api/system/recover", { data: { confirmation: "START PAPER" }, headers })).status()).toBe(400);
  }
});
