import { useState } from "react";
import { api } from "../services/api";
import { money } from "../types";
import { Button } from "./ui/button";
type Snapshot = {
  items: { symbol: string; exchange: string; code: string; price: number; changePercent: number; volume: number }[];
  universeCount: number; quotedCount: number; scanStartedUtc: string; fetchedUtc: string; scope: string; freshness: string;
};
export function TopGainers() {
  const [data, setData] = useState<Snapshot>();
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  async function refresh() {
    setBusy(true); setError(""); setData(undefined);
    try { setData(await api<Snapshot>("/market/top-gainers")); }
    catch (e) { setError((e as Error).message); }
    finally { setBusy(false); }
  }
  const date = (value: string) => new Date(value).toLocaleString("en-IN", { timeZone: "Asia/Kolkata" });
  return <section className="panel gainers-panel">
    <div className="panel-heading"><h2>Top 10 gainers · all market caps</h2><Button variant="outline" onClick={refresh} disabled={busy}>{busy ? "Scanning broker equities…" : "Refresh gainers"}</Button></div>
    <p>Largest positive percentage moves versus previous close across broker-listed NSE/BSE cash equities, with no market-cap filter. Research only; no orders are placed.</p>
    {!data && !error && <p>{busy ? "A full scan can take a few minutes. Keep this page open." : "Refresh to fetch a broker snapshot using this environment’s configured token."}</p>}
    {error && <p role="alert" className="error">{error}</p>}
    {data && <><p>Scan: {date(data.scanStartedUtc)} to {date(data.fetchedUtc)} IST. Coverage: {data.quotedCount.toLocaleString()} / {data.universeCount.toLocaleString()} equities with usable traded quotes.</p>
      {data.quotedCount < data.universeCount && <p className="notice">Partial quote coverage: ranks apply only to available traded quotes, not the complete market.</p>}
      <div className="table-scroll"><table><thead><tr><th>Rank</th><th>Symbol</th><th>Exchange</th><th>Price</th><th>Change</th><th>Volume</th></tr></thead><tbody>{data.items.map((item, i) => <tr key={item.code}><td>{i + 1}</td><td>{item.symbol}</td><td>{item.exchange}</td><td>{money(item.price)}</td><td className="positive">+{item.changePercent.toFixed(2)}%</td><td>{item.volume.toLocaleString("en-IN")}</td></tr>)}</tbody></table></div>
      {data.items.length === 0 && <p>No positive gainers in the available traded quotes.</p>}
      <p className="footnote">{data.scope}</p><p className="footnote">{data.freshness}</p>
    </>}
  </section>;
}
