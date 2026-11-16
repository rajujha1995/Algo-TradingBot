import { useState } from "react";
import { api } from "../services/api";
import { fundsFields, parseFunds } from "../services/brokerFunds";
import { money } from "../types";
import { Button } from "./ui/button";

export function BrokerBalance({ onMonitor }: { onMonitor?: () => void }) {
  const [funds, setFunds] = useState<Record<string, number | null>>();
  const [fetched, setFetched] = useState("");
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  async function refresh() {
    setBusy(true); setError(""); setFunds(undefined); setFetched("");
    try {
      setFunds(parseFunds(await api("/broker/funds")));
      setFetched(new Date().toLocaleString("en-IN", { timeZone: "Asia/Kolkata" }));
    } catch (e) { setError((e as Error).message); }
    finally { setBusy(false); }
  }
  return <section className="panel broker-balance">
    <div className="panel-heading"><h2>Actual INDstocks account balance</h2>
      <Button variant="outline" onClick={refresh} disabled={busy}>{busy ? "Fetching…" : "Refresh broker balance"}</Button></div>
    <p>Read-only snapshot from your broker account for this environment. Refresh manually; this is not a streaming balance or a live holdings valuation.</p>
    {onMonitor && <p><Button variant="outline" onClick={onMonitor}>Open real-time account monitor</Button></p>}
    {error && <p className="error" role="alert">{error}</p>}
    {!funds && !error && <p>Balance not fetched yet. A configured, valid broker token is required.</p>}
    {funds && <><p role="status">Fetched {fetched} IST</p><dl>{fundsFields.map(([key, label]) => <div key={key}><dt>{label}</dt><dd>{funds[key] === null ? "Not provided" : money(funds[key])}</dd></div>)}</dl></>}
    <p className="footnote">Paper mode uses virtual funds and simulated orders. Your real funds are untouched. Paper figures below are separate from this broker snapshot.</p>
  </section>;
}
