import { useCallback, useEffect, useRef, useState } from "react";
import { HubConnectionBuilder, LogLevel } from "@microsoft/signalr";
import {
  Activity,
  ArrowDownUp,
  ChartNoAxesCombined,
  ChevronRight,
  LayoutDashboard,
  ListFilter,
  Newspaper,
  OctagonX,
  Settings2,
  ShieldCheck,
  Wallet,
  FlaskConical,
  Terminal,
  LogOut,
} from "lucide-react";
import * as echarts from "echarts/core";
import { LineChart } from "echarts/charts";
import { GridComponent, TooltipComponent } from "echarts/components";
import { CanvasRenderer } from "echarts/renderers";
echarts.use([LineChart, GridComponent, TooltipComponent, CanvasRenderer]);
import { Button } from "../components/ui/button";
import { api, refreshCsrf } from "../services/api";
import { BrokerBalance } from "../components/BrokerBalance";
import { TradingControls, type TradingStatus } from "../components/TradingControls";
import { TopGainers } from "../components/TopGainers";
import { ObservationJournal } from "../components/ObservationJournal";
import { BrokerAccount } from "../components/BrokerAccount";
import {
  money,
  time,
  type Portfolio,
  type Settings,
  type Control,
} from "../types";
const pages = [
  ["Dashboard", LayoutDashboard],
  ["Trading", Activity],
  ["Broker Account", Wallet],
  ["TradeList", ListFilter],
  ["Market News", Newspaper],
  ["Positions", Wallet],
  ["Orders", ArrowDownUp],
  ["Trades", ChartNoAxesCombined],
  ["Strategy", Settings2],
  ["Backtesting", FlaskConical],
  ["Settings", Settings2],
  ["System Logs", Terminal],
  ["System Health", Activity],
] as const;
type Row = Record<string, string | number | boolean>;
export default function App() {
  const [authenticated, setAuthenticated] = useState(false),
    [password, setPassword] = useState(""),
    [error, setError] = useState(""),
    [busy, setBusy] = useState(false);
  const [page, setPage] = useState("Dashboard"),
    [portfolio, setPortfolio] = useState<Portfolio>(),
    [settings, setSettings] = useState<Settings>(),
    [control, setControl] = useState<Control>(),
    [feed, setFeed] = useState("Connecting"),
    [updated, setUpdated] = useState("");
  const [trading, setTrading] = useState<TradingStatus>();
  const load = useCallback(async () => {
    const [p, s, c, t] = await Promise.all([
      api<Portfolio>("/dashboard"),
      api<Settings>("/settings"),
      api<Control>("/system/status"),
      api<TradingStatus>("/trading/status"),
    ]);
    setPortfolio(p);
    setSettings(s);
    setControl(c);
    setTrading(t);
    setUpdated(
      new Date().toLocaleTimeString("en-IN", { timeZone: "Asia/Kolkata" }),
    );
  }, []);
  useEffect(() => {
    api<Settings>("/settings")
      .then(async (s) => {
        setSettings(s);
        setAuthenticated(true);
        await refreshCsrf();
      })
      .catch(() => {});
  }, []);
  useEffect(() => {
    if (!authenticated) return;
    let cancelled = false;
    load().catch((e) => setError(e.message));
    const connection = new HubConnectionBuilder()
      .withUrl("/hubs/dashboard")
      .withAutomaticReconnect()
      .configureLogging(LogLevel.None)
      .build();
    connection.on("refresh", () => load().catch((e) => setError(e.message)));
    connection.on("marketRefresh", () => window.dispatchEvent(new Event("market-refresh")));
    connection.onreconnecting(() => setFeed("Reconnecting"));
    connection.onreconnected(() => {
      setFeed("Connected");
      load().catch((e) => setError(e.message));
    });
    connection.onclose(() => setFeed("Disconnected"));
    connection
      .start()
      .then(() => {
        if (!cancelled) setFeed("Connected");
      })
      .catch(() => {
        if (!cancelled) setFeed("Disconnected");
      });
    const polling = setInterval(
      () => load().catch((e) => setError(e.message)),
      15000,
    );
    return () => {
      cancelled = true;
      clearInterval(polling);
      void connection.stop();
    };
  }, [authenticated, load]);
  async function login(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError("");
    try {
      await refreshCsrf();
      await api("/auth/login", { password });
      setPassword("");
      await refreshCsrf();
      setAuthenticated(true);
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }
  async function stop() {
    setBusy(true);
    setError("");
    try {
      await api("/system/emergency-stop", {});
      await load();
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }
  if (!authenticated)
    return (
      <main className="login">
        <div className="login-card">
          <div className="brand">
            <div className="brand-mark">i</div>
            <span>
              INDstocks <small>TRADING WORKSPACE</small>
            </span>
          </div>
          <p className="eyebrow">YOUR LOCAL TRADING DESK</p>
          <h1>Welcome to your workspace.</h1>
          <p>
            Sign in with the local password from your backend environment file.
            Broker credentials stay on the backend.
          </p>
          <form onSubmit={login}>
            <label htmlFor="password">Operator password</label>
            <input
              id="password"
              type="password"
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              autoComplete="current-password"
              required
            />
            <Button disabled={busy}>
              {busy ? "Connecting…" : "Open workspace"}{" "}
              <ChevronRight size={16} />
            </Button>
          </form>
          {error && (
            <div role="alert" className="error">
              {error}
            </div>
          )}
          <div className="login-note">
            <ShieldCheck size={16} /> Paper trading by default · Hosted on your
            laptop
          </div>
        </div>
      </main>
    );
  return (
    <div className="shell">
      <aside>
        <div className="brand">
          <div className="brand-mark">i</div>
          <span>
            INDstocks <small>TRADING WORKSPACE</small>
          </span>
        </div>
        <div className="workspace-label">WORKSPACE</div>
        <nav>
          {pages.map(([name, Icon]) => (
            <button
              key={name}
              className={page === name ? "active" : ""}
              onClick={() => {
                setPage(name);
                setError("");
              }}
            >
              <Icon size={18} />
              {name}
            </button>
          ))}
        </nav>
        <div className="sidebar-note">
          <ShieldCheck size={20} />
          <strong>Browse & learn</strong>
          <p>
            Trading starts separately.
            <br />
            Your real funds are untouched.
          </p>
        </div>
        <button
          className="logout"
          onClick={async () => {
            await api("/auth/logout", {});
            setAuthenticated(false);
          }}
        >
          <LogOut size={16} /> Sign out
        </button>
      </aside>
      <div className="workspace">
        <header>
          <div className="breadcrumb">
            Workspace <ChevronRight size={14} />
            <strong>{page}</strong>
          </div>
          <div className="header-right">
            <span className="environment">PROD</span>
            <span className="pill">{trading?.requestedMode === "LIVE" ? "LIVE REQUESTED · BLOCKED" : "● PAPER"}</span>
            <TradingControls kind="mode" status={trading} running={control?.state === "PAPER_RUNNING" || control?.state === "LIVE_RUNNING" || control?.state === "OBSERVING"} onChange={load} />
            <Button variant="destructive" onClick={stop} disabled={busy}>
              <OctagonX size={16} /> EMERGENCY STOP
            </Button>
          </div>
        </header>
        <main className="content">
          <div className="page-heading">
            <div>
              <div className="eyebrow">
                TRADING WORKSPACE / {page.toUpperCase()}
              </div>
              <h1>
                {page === "Dashboard" ? "Your portfolio, at a glance." : page}
              </h1>
              <p>
                {page === "Dashboard"
                  ? "A clear view of your capital, positions and trading activity."
                  : page === "Broker Account" ? "Actual broker account monitoring, separate from paper research and real-money execution." : "Monitor and manage your local paper-trading workspace."}
              </p>
            </div>
            <div className="last-updated">
              Updated {updated || "—"} IST
              <br />
              <span className={feed === "Connected" ? "positive" : "muted"}>
                ● Dashboard {feed.toLowerCase()}
              </span>
            </div>
          </div>
          {error && (
            <div className="error" role="alert">
              {error}
            </div>
          )}
          <div className="status-strip">
            <div>
              <span className="dot" /> Engine{" "}
              <strong>{control?.state || "Loading"}</strong>
            </div>
            <div>
              Market data <strong>{control?.state === "OBSERVING" ? "BROKER OBSERVATION · SEE TRADING" : "Paper ledger simulated"}</strong>
            </div>
            <div>
              Broker{" "}
              <strong>
                {settings?.brokerTokenConfigured
                  ? "Token configured · unverified"
                  : "Not connected"}
              </strong>
            </div>
            <div>
              Live trading <strong>Disabled</strong>
            </div>
          </div>
          {control && control.state !== "PAPER_RUNNING" && control.state !== "OBSERVING" && (
            <div className="notice">
              <ShieldCheck size={20} />
              <div>
                <strong>
                  {control.state === "EMERGENCY_STOPPED"
                    ? "Emergency stop is active."
                    : "Paper engine is stopped."}
                </strong>
                <p>{control.reason}</p>
              </div>
              {page !== "Trading" && <Button variant="outline" onClick={() => setPage("Trading")}>Open Trading</Button>}
            </div>
          )}
          {page === "Dashboard" && portfolio && (
            <>
              <BrokerBalance onMonitor={() => setPage("Broker Account")} />
              <TopGainers />
              <h2>Paper account · virtual funds</h2>
              <div className="kpi-grid">
                {[
                  [
                    "Total account value",
                    money(portfolio.totalAccountValue),
                    "Cash + marked holdings",
                  ],
                  [
                    "Invested amount",
                    money(portfolio.investedAmount),
                    "Cost basis, including entry fees",
                  ],
                  [
                    "Current portfolio value",
                    money(portfolio.currentPortfolioValue),
                    "Simulated holdings only",
                  ],
                  [
                    "Unrealized P&L",
                    money(portfolio.unrealizedPnl),
                    `${portfolio.unrealizedPercent.toFixed(2)}% on invested amount`,
                  ],
                  [
                    "Realized P&L",
                    money(portfolio.realizedPnl),
                    "Net of estimated costs",
                  ],
                  [
                    "Today’s realized net P&L",
                    money(portfolio.todayNetPnl),
                    "IST session · realized only",
                  ],
                  [
                    "Available cash",
                    money(portfolio.availableCash),
                    "After pending commitments",
                  ],
                  [
                    "Total charges",
                    money(portfolio.totalCharges),
                    "Indicative fees and taxes",
                  ],
                  [
                    "Open positions",
                    portfolio.openPositions,
                    `Maximum ${settings?.maxOpenPositions ?? 1}`,
                  ],
                  [
                    "Active orders",
                    portfolio.activeOrders,
                    `${money(portfolio.reservedCash)} reserved`,
                  ],
                ].map(([label, value, sub]) => (
                  <div className="kpi" key={label}>
                    <span>{label}</span>
                    <strong>{value}</strong>
                    <small>{sub}</small>
                  </div>
                ))}
              </div>
              <div className="dashboard-panels">
                <section className="panel">
                  <div className="panel-heading">
                    <h2>Paper trading activity</h2>
                    <span className="tag">SIMULATION</span>
                  </div>
                  <div className="empty-state">
                    <ChartNoAxesCombined size={34} />
                    <h3>Your next step starts here.</h3>
                    <p>
                      Practice order execution, partial fills and emergency
                      stops with a simulated instrument. Open Orders to submit a
                      paper order.
                    </p>
                    <Button variant="outline" onClick={() => setPage("Orders")}>
                      Explore paper orders <ChevronRight size={15} />
                    </Button>
                  </div>
                </section>
                <section className="panel">
                  <div className="panel-heading">
                    <h2>Risk guardrails</h2>
                    <ShieldCheck size={19} />
                  </div>
                  <dl>
                    <div>
                      <dt>Capital limit</dt>
                      <dd>{money(settings?.capitalLimit ?? 1000)}</dd>
                    </div>
                    <div>
                      <dt>Daily planned loss</dt>
                      <dd>{money(settings?.dailyLossLimit ?? 50)}</dd>
                    </div>
                    <div>
                      <dt>Total planned loss</dt>
                      <dd>{money(settings?.totalLossLimit ?? 100)}</dd>
                    </div>
                    <div>
                      <dt>Leverage & derivatives</dt>
                      <dd>Disabled</dd>
                    </div>
                  </dl>
                  <p className="footnote">
                    Planned loss limits cannot guarantee maximum losses. Values
                    and costs are illustrative.
                  </p>
                </section>
              </div>
              <p className="footnote">{portfolio.valuationStatus}</p>
            </>
          )}
          {page === "Orders" && (
            <PaperOrders
              running={control?.state === "PAPER_RUNNING"}
              onChange={load}
            />
          )}
          {page === "Trading" && <><section className="panel trading-panel">
            <div className="panel-heading"><h2>Trading session</h2><span className="tag">{trading?.requestedMode || "Loading"}</span></div>
            <p>Browse balances, gainers and research freely. Selecting a mode does not start trading. Start Trading requires a separate confirmation.</p>
            {trading?.requestedMode === "LIVE" && <p className="error">{trading.blocker}</p>}
            {trading?.requestedMode === "PAPER" && <p>Start Trading begins real-market-data observation for Reliance, ICICI Bank and SBI. It records technical setups and hypothetical ten-share outcomes; no real or DEMO orders are placed.</p>}
            <TradingControls kind="start" status={trading} running={control?.state === "PAPER_RUNNING" || control?.state === "LIVE_RUNNING" || control?.state === "OBSERVING"} onChange={load} />
            <p className="footnote">Start window: 7:00 AM–6:00 PM IST. The observer waits for fresh trades in normal weekday cash hours, 09:15–15:30 IST, and finishes at 15:30. Special sessions are not supported. Emergency Stop ends observation without inventing an exit for open scenarios. Start manually on each observation day.</p>
          </section><ObservationJournal /></>}
          {page === "Positions" && <DataTable path="/positions" />}
          {page === "Broker Account" && <BrokerAccount />}
          {page === "Trades" && <DataTable path="/trades" />}
          {page === "System Logs" && <DataTable path="/logs" />}
          {page === "TradeList" && (
            <section className="panel">
              <div className="panel-heading">
                <h2>Observation watchlist</h2>
                <span className="tag">RELIANCE · ICICIBANK · SBIN</span>
              </div>
              <div className="empty-state">
                <ListFilter size={32} />
                <h3>Real-data research on the Trading page</h3>
                <p>
                  Start a PAPER observation session to assess completed candles and record qualifying what-if setups. Broad-market automatic screening is not implemented; these three symbols form the initial research watchlist.
                </p>
              </div>
              <Button variant="outline" onClick={() => setPage("Trading")}>Open observation journal</Button>
            </section>
          )}
          {page === "Market News" && (
            <section className="panel">
              <div className="empty-state">
                <Newspaper size={32} />
                <h3>No news provider configured</h3>
                <p>
                  A licensed news or official disclosure provider is required.
                  No synthetic headlines are shown.
                </p>
              </div>
            </section>
          )}
          {page === "Strategy" && (
            <section className="panel text-panel">
              <h2>Research before execution</h2>
              <p>
                The Backtesting page provides deterministic SMA momentum
                research with next-candle execution, indicative costs and
                slippage. It does not execute an automated trading strategy.
              </p>
              <p>
                The Trading page provides Breakout-v1 observation on three verified NSE symbols: completed-candle rules, next observed tick assumptions, stop/target scenarios and a persistent journal. These independent what-if scenarios do not execute orders. Broad screening, news/index confirmation and special-session calendars remain unavailable.
              </p>
              <Button variant="outline" onClick={() => setPage("Backtesting")}>
                Open backtesting
              </Button>
            </section>
          )}
          {page === "Backtesting" && <Backtesting />}
          {page === "Settings" && settings && (
            <>
              <section className="panel text-panel">
                <h2>Environment & risk settings</h2>
                <p>
                  Change backend configuration and restart the application to
                  apply settings. Credentials are never returned to this page.
                </p>
                <dl>
                  {Object.entries(settings).map(([k, v]) => (
                    <div key={k}>
                      <dt>{k}</dt>
                      <dd>{String(v)}</dd>
                    </div>
                  ))}
                </dl>
              </section>
              <BrokerPanel />
            </>
          )}
          {page === "System Health" && <JsonPanel path="/system/health" />}
          <footer>
            INDstocks trading workspace{" "}
            <span>Local workspace · Research & simulation</span>
          </footer>
        </main>
      </div>
    </div>
  );
}
function DataTable({ path, version = 0 }: { path: string; version?: number }) {
  const [rows, setRows] = useState<Row[]>([]),
    [error, setError] = useState("");
  useEffect(() => {
    api<Row[]>(path)
      .then(setRows)
      .catch((e) => setError(e.message));
  }, [path, version]);
  return (
    <section className="panel">
      <div className="panel-heading">
        <h2>{path.slice(1).replace("logs", "System events")}</h2>
        <span className="tag">{rows.length} RECORDS</span>
      </div>
      {error ? (
        <p role="alert">{error}</p>
      ) : rows.length === 0 ? (
        <div className="empty-state">
          <ArrowDownUp size={30} />
          <h3>No records yet</h3>
          <p>Your paper-trading activity will appear here.</p>
        </div>
      ) : (
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                {Object.keys(rows[0]).map((k) => (
                  <th key={k}>{k}</th>
                ))}
              </tr>
            </thead>
            <tbody>
              {rows.map((r, i) => (
                <tr key={String(r.id ?? i)}>
                  {Object.entries(r).map(([k, v]) => (
                    <td key={k}>
                      {k.endsWith("Utc") ? time(String(v)) : String(v)}
                    </td>
                  ))}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}
function PaperOrders({
  running,
  onChange,
}: {
  running: boolean;
  onChange: () => Promise<void>;
}) {
  const [side, setSide] = useState("BUY"),
    [quantity, setQuantity] = useState(2),
    [price, setPrice] = useState(100),
    [partial, setPartial] = useState(false),
    [busy, setBusy] = useState(false),
    [error, setError] = useState(""),
    [version, setVersion] = useState(0);
  // Keep the same id after an uncertain network response; editing the payload starts a new request.
  const pendingId = useRef<{ signature: string; id: string } | undefined>(
    undefined,
  );
  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setError("");
    setBusy(true);
    const signature = JSON.stringify({ side, quantity, price, partial });
    if (pendingId.current?.signature !== signature)
      pendingId.current = { signature, id: crypto.randomUUID() };
    try {
      await api("/paper/orders", {
        clientOrderId: pendingId.current.id,
        symbol: "DEMO",
        side,
        quantity,
        price,
        simulatePartialFill: partial,
      });
      pendingId.current = undefined;
      await onChange();
      setVersion((v) => v + 1);
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }
  return (
    <>
      <section className="panel text-panel">
        <h2>Simulated paper order</h2>
        <p>
          DEMO is a synthetic instrument. Entered prices are simulated, with
          configured slippage and indicative fees. Partial remainders fill after
          up to 10 seconds while the engine is running.
        </p>
        <form className="order-form" onSubmit={submit}>
          <label>
            Side
            <select value={side} onChange={(e) => setSide(e.target.value)}>
              <option>BUY</option>
              <option>SELL</option>
            </select>
          </label>
          <label>
            Quantity
            <input
              type="number"
              min="1"
              max="1000"
              value={quantity}
              onChange={(e) => setQuantity(+e.target.value)}
              required
            />
          </label>
          <label>
            Simulated price (₹)
            <input
              type="number"
              min="0.01"
              step="0.01"
              value={price}
              onChange={(e) => setPrice(+e.target.value)}
              required
            />
          </label>
          <label className="checkbox">
            <input
              type="checkbox"
              checked={partial}
              onChange={(e) => setPartial(e.target.checked)}
            />{" "}
            Simulate partial fill
          </label>
          <Button disabled={!running || busy}>
            {busy ? "Submitting…" : "Submit paper order"}
          </Button>
        </form>
        {!running && (
          <p className="footnote">Start a paper session before submitting.</p>
        )}
        {error && (
          <p role="alert" className="error">
            {error}
          </p>
        )}
      </section>
      <DataTable path="/orders" version={version} />
    </>
  );
}
function JsonPanel({ path }: { path: string }) {
  const [data, setData] = useState<unknown>(),
    [error, setError] = useState("");
  useEffect(() => {
    api(path)
      .then(setData)
      .catch((e) => setError(e.message));
  }, [path]);
  return (
    <section className="panel text-panel">
      <h2>System readiness</h2>
      {error ? (
        <p role="alert">{error}</p>
      ) : (
        <pre>{JSON.stringify(data, null, 2)}</pre>
      )}
    </section>
  );
}
function BrokerPanel() {
  const [result, setResult] = useState<unknown>(),
    [error, setError] = useState(""),
    [busy, setBusy] = useState(false);
  return (
    <section className="panel text-panel">
      <h2>INDstocks read-only connection</h2>
      <p>
        Uses the token in your backend environment file. This request only reads
        your profile; it never submits an order.
      </p>
      <Button
        variant="outline"
        disabled={busy}
        onClick={async () => {
          setBusy(true);
          setError("");
          try {
            await api("/broker/profile");
            setResult("Profile retrieved successfully. Token accepted.");
          } catch (e) {
            setError((e as Error).message);
          } finally {
            setBusy(false);
          }
        }}
      >
        Test broker connection
      </Button>
      {!!result && <p className="positive">{String(result)}</p>}
      {error && (
        <p className="error" role="alert">
          {error}
        </p>
      )}
    </section>
  );
}
function Backtesting() {
  const [input, setInput] = useState(""),
    [result, setResult] = useState<Record<string, unknown>>(),
    [error, setError] = useState(""),
    [busy, setBusy] = useState(false);
  const chart = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!result || !chart.current) return;
    const c = echarts.init(chart.current);
    c.setOption({
      color: ["#14756c"],
      tooltip: { trigger: "axis" },
      grid: { left: 60, right: 30, top: 25, bottom: 30 },
      xAxis: {
        type: "category",
        data: (result.equityCurve as number[]).map((_, i) => i + 1),
      },
      yAxis: { type: "value", scale: true },
      series: [
        {
          name: "Equity (₹)",
          type: "line",
          data: result.equityCurve,
          areaStyle: { opacity: 0.08 },
          symbol: "none",
        },
      ],
    });
    const observer = new ResizeObserver(() => c.resize());
    observer.observe(chart.current);
    return () => {
      observer.disconnect();
      c.dispose();
    };
  }, [result]);
  return (
    <>
      <section className="panel text-panel">
        <h2>Historical strategy research</h2>
        <p>
          Upload a JSON array of adjusted, session-filtered candles with UTC
          timestamps. Each candle needs timestampUtc, open, high, low, close and
          volume. At least seven candles are required. See
          docs/TRADING_STRATEGY.md for the exact format.
        </p>
        <input
          aria-label="Historical candle JSON"
          type="file"
          accept=".json"
          onChange={async (e) => {
            const f = e.target.files?.[0];
            if (f) {
              if (f.size > 2000000) {
                setError("Maximum file size is 2 MB.");
                return;
              }
              setInput(await f.text());
            }
          }}
        />
        <Button
          disabled={!input || busy}
          onClick={async () => {
            setBusy(true);
            setError("");
            try {
              setResult(
                await api<Record<string, unknown>>("/backtesting", {
                  candles: JSON.parse(input),
                  lookback: 5,
                }),
              );
            } catch (e) {
              setError((e as Error).message);
            } finally {
              setBusy(false);
            }
          }}
        >
          Run backtest
        </Button>
        {error && (
          <p className="error" role="alert">
            {error}
          </p>
        )}
      </section>
      {result && (
        <section className="panel text-panel">
          <h2>Research results</h2>
          <dl>
            {Object.entries(result)
              .filter(([k]) => k !== "equityCurve" && k !== "assumptions")
              .map(([k, v]) => (
                <div key={k}>
                  <dt>{k}</dt>
                  <dd>{String(v)}</dd>
                </div>
              ))}
          </dl>
          <div ref={chart} style={{ height: 300 }} />
          <p className="footnote">
            {String(result.assumptions)} A backtest does not establish future
            profitability.
          </p>
        </section>
      )}
    </>
  );
}
