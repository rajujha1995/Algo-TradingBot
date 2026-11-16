import { useCallback, useEffect, useRef, useState } from "react";
import { TrendingUp, RefreshCw } from "lucide-react";
import { api } from "../services/api";
import { money } from "../types";
import { Button } from "./ui/button";
type Snapshot = { items: { symbol: string; exchange: string; code: string; price: number; changePercent: number; volume: number; previousClose?: number | null; todayClose?: number | null; closeDate?: string | null; closeStatus?: string; latestClose?: number | null; latestCloseDate?: string | null }[]; universeCount: number; quotedCount: number; rejectedCount?: number; unresolvedCount?: number; scanStartedUtc: string; fetchedUtc: string; scope: string; freshness: string };
export function TopGainers() {
  const [data, setData] = useState<Snapshot>(), [error, setError] = useState(""), [busy, setBusy] = useState(false);
  const inFlight = useRef(false), alive = useRef(true);
  const refresh = useCallback(async () => { if (inFlight.current) return; inFlight.current = true; setBusy(true); setError(""); try { const result = await api<Snapshot>("/market/top-gainers"); if (alive.current) setData(result); } catch(e) { if (alive.current) setError((e as Error).message); } finally { inFlight.current = false; if (alive.current) setBusy(false); } }, []);
  useEffect(() => { alive.current = true; void refresh(); const timer = setInterval(refresh, 180000); return () => { alive.current = false; clearInterval(timer); }; }, [refresh]);
  const date = (s: string) => new Date(s).toLocaleString("en-IN", { timeZone:"Asia/Kolkata" });
  const peak = Math.max(1, ...(data?.items.map(x => x.changePercent) || []));
  return <section className="panel gainers-panel market-movers"><div className="panel-heading"><div><p className="eyebrow"><TrendingUp size={14}/>MARKET MOVERS</p><h2>Top 10 gainers <span className="heading-muted">across market caps</span></h2></div><Button variant="outline" onClick={refresh} disabled={busy}><RefreshCw size={14} className={busy ? "refresh-spin" : ""}/>{busy ? "Scanning…" : "Refresh gainers"}</Button></div>
    <p>Broker-listed NSE/BSE cash equities, ranked by positive change versus previous close. Latest quotes and dated daily closing prices are kept separate.</p>
    {error && <p className="error" role="alert">{error} {data ? "Displayed scan is older; check its fetch time." : "No market data substituted."}</p>}
    {!data && !error && <div className="empty-state"><TrendingUp size={32}/><h3>Reading the market universe…</h3><p>The full broker scan and daily closes can take a few minutes.</p></div>}
    {data && <><div className="mover-meta"><span>Snapshot {date(data.fetchedUtc)} IST</span><span>{data.quotedCount.toLocaleString()} / {data.universeCount.toLocaleString()} usable traded quotes</span><span>Auto-refresh while open · about 3 min</span></div>{data.quotedCount < data.universeCount && <p className="notice">Partial coverage: ranks apply only to available quotes.</p>}
      {!!((data.rejectedCount ?? 0) + (data.unresolvedCount ?? 0)) && <p className="notice">Broker rejected {data.rejectedCount ?? 0} instrument codes. {data.unresolvedCount ?? 0} instruments could not be checked within this scan. Rankings cover successfully returned quotes only.</p>}
      <div className="table-scroll"><table><thead><tr><th>Rank / instrument</th><th>Change</th><th>Latest quote</th><th>Previous close</th><th>Today's close · broker</th><th>Volume</th></tr></thead><tbody>{data.items.map((item, i) => <tr key={item.code}><td><div className="mover-instrument"><span className="mover-rank">{String(i + 1).padStart(2,"0")}</span><div><strong>{item.symbol}</strong><small>{item.exchange}</small></div></div></td><td><strong className="positive">+{item.changePercent.toFixed(2)}%</strong><span className="move-bar"><span style={{width:`${Math.min(100, item.changePercent / peak * 100)}%`}}/></span></td><td>{money(item.price)}</td><td>{item.previousClose == null ? "Unavailable" : money(item.previousClose)}</td><td><strong>{item.todayClose == null ? "Pending / unavailable" : money(item.todayClose)}</strong><small>{item.closeDate || item.closeStatus || "Date not verified"}</small>{item.todayClose == null && item.latestClose != null && <small>Last completed {item.latestCloseDate}: {money(item.latestClose)}</small>}</td><td>{item.volume.toLocaleString("en-IN")}</td></tr>)}</tbody></table></div>
      {!data.items.length && <p>No positive movers in the available traded quotes.</p>}<p className="footnote">{data.scope}</p><p className="footnote">{data.freshness}</p></>}
  </section>;
}
