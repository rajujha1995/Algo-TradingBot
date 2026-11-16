import { useEffect, useRef, useState } from "react";
import { api } from "../services/api";
import { fundsFields, parseFunds } from "../services/brokerFunds";
import { money } from "../types";
import { Button } from "./ui/button";
type Section = { data: { status: string; data: unknown } | null; fetchedUtc: string | null; error: string | null };
type Holding = { symbol: string; code: string | null; quantity: number | null; averagePrice: number | null; price: number | null; priceUtc: string | null; marketValue: number | null; unrealizedPnl: number | null; fresh: boolean; note: string };
type Snapshot = {
  enabled: boolean; serverUtc: string; sections: Record<string, Section>; holdingMarks: Holding[]; positionMarks: Holding[];
  watchlist: { symbol: string; code: string; price: number | null; priceUtc: string | null; fresh: boolean }[];
  priceFeed: string; mappingStatus: string; policy: string;
  orderFeed: { connected: boolean; reconciled: boolean; status: string; limitation: string; events: { orderId: string; publishedUtc: string; receivedUtc: string; status: string; name: string | null; side: string | null; requestedQuantity: number | null; filledLots: number | null; executionPrice: number | null }[] };
};
const stamp = (value: string | null | undefined) => value ? new Date(/[zZ]$|[+-]\d\d:\d\d$/.test(value) ? value : value + "Z").toLocaleString("en-IN", { timeZone: "Asia/Kolkata" }) + " IST" : "Not fetched";
const amount = (value: number | null | undefined) => value === null || value === undefined ? "Not available" : money(value);

export function BrokerAccount() {
  const [data, setData] = useState<Snapshot>();
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  const receivedAt = useRef(performance.now());
  const [, tick] = useState(0);
  useEffect(() => { const timer = setInterval(() => tick(x => x + 1), 1000); return () => clearInterval(timer); }, []);
  useEffect(() => {
    let cancelled = false, loading = false;
    async function load() {
      if (loading) return; loading = true;
      try { const result = await api<Snapshot>("/broker/account"); if (!cancelled) { receivedAt.current = performance.now(); setData(result); setError(""); } }
      catch (e) { if (!cancelled) setError((e as Error).message); }
      finally { loading = false; }
    }
    void load(); const fallback = setInterval(load, 5000);
    window.addEventListener("market-refresh", load);
    return () => { cancelled = true; clearInterval(fallback); window.removeEventListener("market-refresh", load); };
  }, []);
  async function toggle() {
    setBusy(true);
    try { const result = await api<Snapshot>("/broker/account/monitor", { enabled: !data?.enabled }); receivedAt.current = performance.now(); setData(result); setError(""); }
    catch (e) { setError((e as Error).message); }
    finally { setBusy(false); }
  }
  let funds: Record<string, number | null> | undefined;
  const elapsed = performance.now() - receivedAt.current;
  const current = data ? Date.parse(data.serverUtc) + elapsed : NaN;
  const freshPrice = (fresh: boolean, source: string | null) => {
    const timestamp = source ? Date.parse(/[zZ]$|[+-]\d\d:\d\d$/.test(source) ? source : source + "Z") : NaN;
    return !error && fresh && timestamp >= current - 15000 && timestamp <= current + 2000;
  };
  try { if (data?.sections.funds?.data) funds = parseFunds(data.sections.funds.data); } catch { /* Section failure is rendered explicitly below. */ }
  return <div className="broker-account">
    <section className="panel research-panel">
      <div className="panel-heading"><h2>Broker account · real data, read-only</h2><Button onClick={toggle} disabled={!data || busy}>{busy ? "Updating…" : data?.enabled ? "Stop account monitoring" : "Start account monitoring"}</Button></div>
      <p>Account monitoring is separate from Start Watching. It reads your actual broker account and streams available prices/order events without placing orders. Broker activity and watched reference-price movements are recorded separately.</p>
      {error && <p role="alert" className="error">{error} Last displayed snapshots may be stale.</p>}
      {data && <><p><strong>{data.enabled ? "Monitoring enabled" : "Monitoring stopped"}</strong> · {data.priceFeed}</p><p>Order feed: {error || elapsed > 10000 ? "Connection state unknown; snapshot stale" : data.orderFeed.status} · {!error && elapsed <= 10000 && data.orderFeed.connected && data.orderFeed.reconciled ? "REST order book checked for this connection" : "Reconciliation pending / stream unverified"}</p>
        <p className="footnote">{data.policy}</p><p className="footnote">{data.mappingStatus}</p>
        {data.watchlist.length > 0 && <div className="research-kpis">{data.watchlist.map(item => <div key={item.code}><span>{item.symbol} · {item.code}</span><strong>{freshPrice(item.fresh, item.priceUtc) ? amount(item.price) : "No fresh price"}</strong><small>{freshPrice(item.fresh, item.priceUtc) ? stamp(item.priceUtc) : "Older than 15 seconds, disconnected, or outside observed activity"}</small></div>)}</div>}
        <h3 className="research-subtitle">Broker funds · periodically refreshed</h3>
        <SectionState section={data.sections.funds} />
        {funds && <dl>{fundsFields.map(([key, label]) => <div key={key}><dt>{label}</dt><dd>{amount(funds[key])}</dd></div>)}</dl>}
      </>}
    </section>
    {data && <>
      <section className="panel research-panel"><div className="panel-heading"><h2>Actual holdings · fresh-price marks</h2></div><SectionState section={data.sections.holdings} /><MarkedPositions rows={data.holdingMarks.map(row => ({ ...row, fresh: freshPrice(row.fresh, row.priceUtc) }))} /><p className="footnote">Quantity and average cost come from the holdings snapshot. Fresh LTP marks are estimates before costs; they do not establish cash available for trading or total account equity.</p></section>
      <section className="panel research-panel"><div className="panel-heading"><h2>Actual positions · cash-equity marks</h2></div><SectionState section={data.sections.positions} /><MarkedPositions rows={data.positionMarks.map(row => ({ ...row, fresh: freshPrice(row.fresh, row.priceUtc) }))} /><AccountRows section={data.sections.positions} columns={[["Symbol", "symbol"], ["Segment", "segment"], ["Product", "product"], ["Net quantity", "net_qty"], ["Broker realized P&L", "realized_profit"]]} /><p className="footnote">Non-equity or ambiguously mapped positions are not live-valued. Broker-reported realized P&L is a REST snapshot; it is not the same as the estimated open-position mark.</p></section>
      <section className="panel research-panel"><div className="panel-heading"><h2>Broker order events · streaming</h2></div><p>{data.orderFeed.status}</p><p className="footnote">{data.orderFeed.limitation}</p>
        {data.orderFeed.events.length === 0 ? <p>No order events received. An idle stream does not mean the account has no orders; check the REST order book below.</p> : <div className="table-scroll"><table><thead><tr><th>Order</th><th>Instrument / side</th><th>Status</th><th>Requested units</th><th>Filled lots</th><th>Execution price</th><th>Published</th></tr></thead><tbody>{data.orderFeed.events.map(e => <tr key={e.orderId}><td>{e.orderId}</td><td>{e.name || "Not provided"} / {e.side || "Not provided"}</td><td>{e.status}</td><td>{e.requestedQuantity ?? "Not provided"}</td><td>{e.filledLots ?? "Not provided"}</td><td>{amount(e.executionPrice)}</td><td>{stamp(e.publishedUtc)}</td></tr>)}</tbody></table></div>}
      </section>
      <section className="panel research-panel"><div className="panel-heading"><h2>Broker order book · current trading day</h2></div><SectionState section={data.sections.orders} /><AccountRows section={data.sections.orders} columns={[["Order", "id"], ["Name", "name"], ["Side", "txn_type"], ["Status", "status"], ["Requested units", "requested_qty"], ["Traded units", "traded_qty"], ["Traded price", "traded_price"], ["Broker updated", "updated_at"]]} /></section>
      <section className="panel research-panel"><div className="panel-heading"><h2>Actual equity fills · current trading day</h2></div><SectionState section={data.sections.trades} /><AccountRows section={data.sections.trades} columns={[["Fill ID", "fill_id"], ["Exchange order", "exch_order_id"], ["Security code", "scrip_code"], ["Quantity", "quantity"], ["Price", "price"], ["Trade time", "trade_date"]]} /></section>
    </>}
  </div>;
}
function SectionState({ section }: { section?: Section }) {
  const age = section?.fetchedUtc ? Date.now() - Date.parse(section.fetchedUtc) : Infinity;
  return <p className={section?.error ? "error" : "footnote"}>Fetched {stamp(section?.fetchedUtc)}{section?.error ? ` · STALE / ERROR: ${section.error}` : age > 60000 ? " · Waiting / older than 60 seconds; do not assume current" : " · REST snapshot"}</p>;
}
function MarkedPositions({ rows }: { rows: Holding[] }) {
  return rows.length === 0 ? <p>No rows in the last successful snapshot, or not fetched yet.</p> : <div className="table-scroll"><table><thead><tr><th>Symbol</th><th>Units</th><th>Broker average</th><th>Fresh LTP</th><th>Estimated value</th><th>Estimated open P&L</th><th>Price time / state</th></tr></thead><tbody>{rows.map((r, i) => <tr key={`${r.code}-${i}`}><td>{r.symbol}</td><td>{r.quantity ?? "Not provided"}</td><td>{amount(r.averagePrice)}</td><td>{r.fresh ? amount(r.price) : "Not available"}</td><td>{r.fresh ? amount(r.marketValue) : "Not available"}</td><td>{r.fresh ? amount(r.unrealizedPnl) : "Not available"}</td><td>{r.fresh ? stamp(r.priceUtc) : r.note}</td></tr>)}</tbody></table></div>;
}
function AccountRows({ section, columns }: { section?: Section; columns: [string, string][] }) {
  const data = section?.data?.data;
  if (!Array.isArray(data)) return <p>No successful snapshot yet.</p>;
  if (data.length === 0) return <p>Broker returned an empty list for this snapshot.</p>;
  function display(row: Record<string, unknown>, key: string) { const value = row[key]; if (typeof value === "string" && (key === "updated_at" || key === "trade_date") && Number.isFinite(Date.parse(value))) return stamp(value); return value === null || value === undefined || value === "" ? "Not provided" : typeof value === "string" || typeof value === "number" ? String(value) : "Not provided"; }
  return <><div className="table-scroll"><table><thead><tr>{columns.map(([name, key]) => <th key={key}>{name}</th>)}</tr></thead><tbody>{data.slice(0, 200).filter(row => row && typeof row === "object").map((row, i) => <tr key={i}>{columns.map(([, key]) => <td key={key}>{display(row, key)}</td>)}</tr>)}</tbody></table></div>{data.length > 200 && <p>Displaying the first 200 of {data.length} broker rows.</p>}</>;
}
