import { test, expect } from "@playwright/test";
import { workspace } from "./realtime-fixtures";

test("one workspace confirms watching, keeps order execution off and displays dated closes", async ({page}) => {
  let started = 0; let state = workspace();
  await page.route("**/api/settings", r => r.fulfill({json:{brokerTokenConfigured:true}}));
  await page.route("**/api/auth/csrf", r => r.fulfill({json:{token:"fixture"}}));
  await page.route("**/api/workspace", r => r.fulfill({json:state}));
  await page.route("**/api/trading/start", r => { expect(r.request().postDataJSON().confirmation).toBe("START WATCHING"); started++; state={...state,state:"WATCHING"}; return r.fulfill({json:{state:"WATCHING"}}); });
  await page.route("**/api/market/top-gainers", r => r.fulfill({json:{items:[{symbol:"FIXTURE_GAINER",code:"NSE_1",exchange:"NSE",price:120,previousClose:100,todayClose:115,closeDate:"2026-11-17",changePercent:20,volume:1000},{symbol:"PENDING_CLOSE",code:"NSE_2",exchange:"NSE",price:110,previousClose:100,todayClose:null,latestClose:100,latestCloseDate:"2026-11-16",changePercent:10,volume:500}],universeCount:5,quotedCount:2,rejectedCount:1,unresolvedCount:2,scanStartedUtc:state.serverUtc,fetchedUtc:state.serverUtc,scope:"Fixture universe",freshness:"Snapshot fixture"}}));
  await page.goto("/");
  await expect(page.getByRole("heading",{name:"A clearer view of the market."})).toBeVisible();
  await expect(page.getByText("Execution off",{exact:true})).toBeVisible();
  await expect(page.getByRole("button",{name:/Go LIVE|PAPER|Start Trading/})).toHaveCount(0);
  const row=page.getByRole("row").filter({hasText:"FIXTURE_GAINER"}); await expect(row).toContainText("₹115.00"); await expect(row).toContainText("2026-11-17");
  const pending=page.getByRole("row").filter({hasText:"PENDING_CLOSE"}); await expect(pending).toContainText("Pending / unavailable"); await expect(pending).toContainText("Last completed 2026-11-16");
  await expect(page.getByText("Broker rejected 1 instrument codes. 2 instruments could not be checked within this scan. Rankings cover successfully returned quotes only.")).toBeVisible();
  await page.screenshot({path:"test-results/realtime-desktop.png",fullPage:true});
  await page.getByRole("button",{name:"Start Watching",exact:true}).click();
  const confirm=page.getByRole("button",{name:"Confirm START WATCHING"}); await expect(confirm).toBeDisabled();
  await page.getByLabel("Type START WATCHING exactly").fill("START TRADING"); await expect(confirm).toBeDisabled(); expect(started).toBe(0);
  await page.getByLabel("Type START WATCHING exactly").fill("START WATCHING"); await confirm.click();
  await expect(page.getByRole("button",{name:"Stop Watching",exact:true})).toBeVisible(); expect(started).toBe(1);
  await page.getByRole("navigation").getByRole("button",{name:"Watch Journal",exact:true}).click();
  await expect(page.getByRole("heading",{name:"Watched suggestions",exact:true})).toBeVisible();
  await expect(page.getByText("Your journal starts with a verified setup.")).toBeVisible();
});

test("start is disabled outside the IST window and mobile overview fits", async ({page}) => {
  await page.setViewportSize({width:390,height:844});
  await page.route("**/api/settings", r=>r.fulfill({json:{brokerTokenConfigured:true}}));
  await page.route("**/api/auth/csrf", r=>r.fulfill({json:{token:"fixture"}}));
  await page.route("**/api/workspace", r=>r.fulfill({json:workspace(14)})); // 19:30 IST
  await page.route("**/api/market/top-gainers", r=>r.fulfill({status:503,json:{error:"No fixture data"}}));
  await page.goto("/"); await expect(page.getByRole("button",{name:"Start Watching",exact:true})).toBeDisabled();
  expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBe(true);
  await page.screenshot({path:"test-results/realtime-mobile.png",fullPage:true});
});

test("removed execution routes cannot place orders", async ({request}) => {
  test.skip(!process.env.E2E_ISOLATED,"Requires disposable API host");
  const csrf=await(await request.get("/api/auth/csrf")).json();
  expect((await request.post("/api/auth/login",{data:{password:process.env.E2E_PASSWORD},headers:{"X-CSRF-TOKEN":csrf.token}})).status()).toBe(200);
  const fresh=await(await request.get("/api/auth/csrf")).json();
  for(const path of ["/api/orders/paper","/api/trading/mode","/api/system/recover"]) expect((await request.post(path,{data:{},headers:{"X-CSRF-TOKEN":fresh.token}})).status()).toBe(404);
  const state=await(await request.get("/api/workspace")).json(); expect(state.executionEnabled).toBe(false); expect(state.entries).toEqual([]);
});
