import { useEffect, useRef, useState } from "react";
import * as echarts from "echarts/core";
import { CandlestickChart } from "echarts/charts";
import { GridComponent, TooltipComponent, DataZoomComponent } from "echarts/components";
import { CanvasRenderer } from "echarts/renderers";
import { api } from "../services/api";
import { money, time } from "../types";
import { Button } from "./ui/button";
echarts.use([CandlestickChart, GridComponent, TooltipComponent, DataZoomComponent, CanvasRenderer]);
type Bar = { timestampUtc: string; open: number; high: number; low: number; close: number; volume: number };
type Evidence = { candles: Bar[]; ema20: number | null; rsi14: number | null; atr14: number | null; relativeVolume: number | null; vwap: number | null; breakoutLevel: number | null; news: string; context: string };
type Suggestion = { id: number; symbol: string; strategy: string; suggestedUtc: string; candleCloseUtc: string; status: string; quantity: number; capitalFitQuantity: number; proposedEntry: number; stop: number; target: number; entryPrice: number | null; entryUtc: string | null; lastPrice: number | null; markUtc: string | null; exitPrice: number | null; exitUtc: string | null; grossPnl: number | null; estimatedCosts: number | null; netPnl: number | null; reason: string; outcomeNote: string; maxFavorable: number; maxAdverse: number };
type Journal = {
  sessions: { id: number; startedUtc: string; status: string; message: string }[];
  assessments: { id: number; symbol: string; assessedUtc: string; candleCloseUtc: string | null; outcome: string; reason: string; evidence: Evidence }[];
  journal: Suggestion[];
  summary: { signals: number; filled: number; closed: number; uncertain: number; realizedScenarioNet: number; knownClosedWinRate: number | null; daysWithObservations: number; observedMinutes: number; fullWatchlistMinutes: number };
  daily: { date: string; observedMinutes: number; signals: number; closed: number; net: number; uncertain: number }[];
  model: string; review: string;
};

export function ObservationJournal() {
  const [data, setData] = useState<Journal>();
  const [error, setError] = useState("");
  const [selected, setSelected] = useState<{ title: string; evidence: Evidence; suggestion?: Suggestion }>();
  const [limit, setLimit] = useState(20);
  const [busy, setBusy] = useState(false);
  useEffect(() => {
    let cancelled = false, loading = false;
    async function load() {
      if (loading) return; loading = true;
      try { const result = await api<Journal>("/research/journal"); if (!cancelled) { setData(result); setError(""); } }
      catch (e) { if (!cancelled) setError((e as Error).message); }
      finally { loading = false; }
    }
    void load(); const timer = setInterval(load, 5000);
    window.addEventListener("market-refresh", load);
    return () => { cancelled = true; clearInterval(timer); window.removeEventListener("market-refresh", load); };
  }, []);
  async function detail(s: Suggestion) {
    setBusy(true);
    try { const result = await api<{ evidence: Evidence; suggestion: Suggestion }>(`/research/suggestions/${s.id}`); setSelected({ title: `${s.symbol} · evidence recorded ${time(s.suggestedUtc)} IST`, ...result }); }
    catch (e) { setError((e as Error).message); }
    finally { setBusy(false); }
  }
  return <div className="observation-journal">
    <section className="panel research-panel">
      <div className="panel-heading"><h2>Real-data paper observation</h2><span className="tag">NO REAL ORDERS</span></div>
      <p>RELIANCE · ICICIBANK · SBIN. Real timestamped INDstocks prices and completed five-minute candles; ten-share what-if fills are simulated. Keep PAPER selected, then Start Trading to begin observation.</p>
      {error && <p className="error" role="alert">{error} Previously displayed observations may be stale.</p>}
      {!data && !error && <p>Loading saved observation journal…</p>}
      {data && <>
        <p><strong>{data.sessions[0]?.status || "Not started"}</strong> · {data.sessions[0]?.message || "Browsing does not collect observations. Start an observation session to begin."}</p>
        <div className="research-kpis">
          <div><span>Days with any observation</span><strong>{data.summary.daysWithObservations} / 5 goal</strong></div>
          <div><span>Observed minutes / all 3 fresh</span><strong>{data.summary.observedMinutes} / {data.summary.fullWatchlistMinutes}</strong></div>
          <div><span>Suggestions / assumed entries</span><strong>{data.summary.signals} / {data.summary.filled}</strong></div>
          <div><span>Closed scenario net P&L</span><strong>{money(data.summary.realizedScenarioNet)}</strong></div>
          <div><span>Closed / uncertain outcomes</span><strong>{data.summary.closed} / {data.summary.uncertain}</strong></div>
          <div><span>Win rate · known closed only</span><strong>{data.summary.knownClosedWinRate === null ? "No closed outcomes" : `${data.summary.knownClosedWinRate.toFixed(1)}%`}</strong></div>
        </div>
        <p className="footnote">{data.review}</p>
        <h3 className="research-subtitle">Latest candle assessments</h3>
        {data.assessments.length === 0 && <p>No assessments yet. The observer waits for normal cash hours, broker connectivity and sufficient completed candles; no candidates are fabricated.</p>}
        <div className="assessment-grid">{data.assessments.map(a => <article key={a.id}>
          <h3>{a.symbol} <small>{a.outcome}</small></h3><p>{a.reason}</p><p className="footnote">Assessed {time(a.assessedUtc)} IST · candle close {a.candleCloseUtc ? time(a.candleCloseUtc) : "unavailable"} IST</p>
          <Button variant="outline" onClick={() => setSelected({ title: `${a.symbol} · latest completed-candle assessment`, evidence: a.evidence })}>View candles & indicators</Button>
        </article>)}</div>
        <details className="research-assumptions"><summary>Rules, fill assumptions and limitations</summary><p>{data.model}</p><p>Breakout-v1: completed close above prior 20-bar high, EMA20 and session VWAP; relative volume ≥1.5×; RSI14 50–75; close in top quarter of the candle range. Minimum 60 completed bars for warmup. Stop below the signal candle or one ATR; 2R target, net reward/risk ≥1.5 after indicative costs. A fresh subsequent tick must pass entry-distance checks within two minutes. No new setups after 15:15; assumed time exit from 15:20 on a fresh observed tick. No guaranteed stop execution or future-return prediction.</p><p>Ten-share scenarios are independent and may exceed the ₹1,000 capital limit; they are not a funded portfolio. Capital-fit quantity is illustrative, not portfolio-wide approval. Missing feed continuity produces UNCERTAIN outcomes rather than invented exits.</p></details>
      </>}
    </section>
    {selected && <section className="panel research-panel"><div className="panel-heading"><h2>{selected.title}</h2><Button variant="outline" onClick={() => setSelected(undefined)}>Close evidence</Button></div>
      {selected.suggestion && <><p>{selected.suggestion.reason}</p><p>Suggested {time(selected.suggestion.suggestedUtc)} IST · {selected.suggestion.strategy} · Status {selected.suggestion.status}</p><p>10-share entry {selected.suggestion.entryPrice === null ? "not assumed" : money(selected.suggestion.entryPrice)} · stop {money(selected.suggestion.stop)} · target {money(selected.suggestion.target)} · indicative costs {selected.suggestion.estimatedCosts === null ? "not available" : money(selected.suggestion.estimatedCosts)}</p><p>{selected.suggestion.outcomeNote}</p></>}
      <CandleEvidence evidence={selected.evidence} />
    </section>}
    <section className="panel research-panel"><div className="panel-heading"><h2>Suggestion journal · what would have happened</h2><span className="tag">PERSISTED LOCALLY</span></div>
      <p>Closed rows use observed assumed exits. OPEN is unrealized; UNCERTAIN retains only its last observation and is excluded from closed P&L. No historical entries are backfilled as if suggested in real time.</p>
      {data?.journal.length === 0 && <p>No qualifying suggestions recorded yet. NO TRADE assessments are a valid outcome.</p>}
      {data && data.journal.length > 0 && <><div className="table-scroll"><table><thead><tr><th>Suggested · IST</th><th>Symbol / qty</th><th>Status</th><th>Proposed / assumed entry</th><th>Stop / target</th><th>Last mark / assumed exit</th><th>Net P&L · estimate</th><th>Evidence</th></tr></thead><tbody>{data.journal.slice(0, limit).map(s => <tr key={s.id}>
        <td>{time(s.suggestedUtc)}</td><td>{s.symbol} · {s.quantity}<br /><small>Capital-fit: {s.capitalFitQuantity}</small></td><td>{s.status}</td><td>{money(s.proposedEntry)} / {s.entryPrice === null ? "No entry" : money(s.entryPrice)}</td><td>{money(s.stop)} / {money(s.target)}</td><td>{s.exitPrice !== null ? money(s.exitPrice) : s.lastPrice !== null ? money(s.lastPrice) : "Not observed"}<br /><small>{s.markUtc ? `${time(s.markUtc)} IST` : ""}</small></td><td>{s.netPnl === null ? "Not entered" : s.status === "UNCERTAIN" ? `Unverified (${money(s.netPnl)} last mark)` : `${money(s.netPnl)}${s.status === "OPEN" ? " unrealized" : ""}`}</td><td><Button variant="outline" disabled={busy} onClick={() => detail(s)}>Why this setup?</Button></td>
      </tr>)}</tbody></table></div>{data.journal.length > limit && <Button variant="outline" onClick={() => setLimit(x => x + 20)}>Show more suggestions</Button>}</>}
    </section>
    <section className="panel research-panel"><div className="panel-heading"><h2>Daily observation review</h2></div>
      {!data?.daily.length && <p>No observed trading days yet. Start collecting live observations; a week cannot be validated retrospectively.</p>}
      {!!data?.daily.length && <div className="table-scroll"><table><thead><tr><th>Date · IST</th><th>Observed minutes</th><th>Suggestions</th><th>Known closed</th><th>Closed scenario net</th><th>Uncertain</th></tr></thead><tbody>{data.daily.map(day => <tr key={day.date}><td>{day.date}</td><td>{day.observedMinutes}</td><td>{day.signals}</td><td>{day.closed}</td><td>{money(day.net)}</td><td>{day.uncertain}</td></tr>)}</tbody></table></div>}
    </section>
  </div>;
}

function CandleEvidence({ evidence }: { evidence: Evidence }) {
  const container = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!container.current || !evidence.candles.length) return;
    const chart = echarts.init(container.current);
    chart.setOption({ animation: false, tooltip: { trigger: "axis", renderMode: "richText" }, grid: { left: 70, right: 24, top: 20, bottom: 70 }, xAxis: { type: "category", data: evidence.candles.map(c => time(c.timestampUtc)) }, yAxis: { scale: true }, dataZoom: [{ type: "inside" }, { type: "slider", bottom: 10 }], series: [{ name: "Completed 5-minute OHLC", type: "candlestick", data: evidence.candles.map(c => [c.open, c.close, c.low, c.high]), itemStyle: { color: "#209579", color0: "#cf5555", borderColor: "#209579", borderColor0: "#cf5555" } }] });
    const observer = new ResizeObserver(() => chart.resize()); observer.observe(container.current);
    return () => { observer.disconnect(); chart.dispose(); };
  }, [evidence]);
  return <><p>Completed broker candles only. Candle labels are opening times in IST. Journal evidence is frozen at suggestion time; it does not include future candles.</p>
    {evidence.candles.length > 0 ? <div className="research-chart" ref={container} aria-label="Completed five-minute candlestick chart" role="img" /> : <p>Insufficient candle data.</p>}
    <div className="research-metrics">{Object.entries({ EMA20: evidence.ema20, RSI14: evidence.rsi14, ATR14: evidence.atr14, "Relative volume": evidence.relativeVolume, VWAP: evidence.vwap, "Prior 20-bar high": evidence.breakoutLevel }).map(([key, value]) => <span key={key}>{key}: <strong>{value === null ? "Not available" : value.toFixed(2)}</strong></span>)}</div>
    <p className="footnote">{evidence.news} {evidence.context}</p>
  </>;
}
