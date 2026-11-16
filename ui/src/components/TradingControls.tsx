import { useEffect, useRef, useState } from "react";
import { api, refreshCsrf } from "../services/api";
import { Button } from "./ui/button";

export type TradingStatus = {
  requestedMode: "PAPER" | "LIVE";
  liveReady: boolean;
  blocker: string;
  serverUtc: string;
  deployment: string;
};

export function TradingControls({ status, running, onChange, kind }: {
  status?: TradingStatus; running: boolean; onChange: () => Promise<void>; kind: "mode" | "start";
}) {
  const [action, setAction] = useState<"LIVE" | "PAPER" | "START">();
  const [text, setText] = useState("");
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  const [, tick] = useState(0);
  const received = useRef({ value: status, at: performance.now() });
  if (received.current.value !== status) received.current = { value: status, at: performance.now() };
  const dialog = useRef<HTMLDialogElement>(null);
  useEffect(() => { if (action) dialog.current?.showModal(); }, [action]);
  useEffect(() => { const timer = setInterval(() => tick(x => x + 1), 1000); return () => clearInterval(timer); }, []);
  const elapsed = performance.now() - received.current.at;
  const now = status ? Date.parse(status.serverUtc) + elapsed : NaN;
  const istMinutes = ((now / 60000 + 330) % 1440 + 1440) % 1440;
  const withinWindow = Number.isFinite(now) && elapsed < 60000 && istMinutes >= 420 && istMinutes < 1080;
  const expected = action === "START" ? "START TRADING" : action === "LIVE" ? "GO LIVE" : "PAPER";
  function open(next: "LIVE" | "PAPER" | "START") { setText(""); setError(""); setAction(next); }
  async function confirm(e: React.FormEvent) {
    e.preventDefault(); setBusy(true); setError("");
    try {
      await api(action === "START" ? "/trading/start" : "/trading/mode", action === "START" ? { confirmation: text } : { mode: action, confirmation: text });
      if (action !== "START") await refreshCsrf();
      await onChange(); setAction(undefined);
    } catch (e) { setError((e as Error).message); }
    finally { setBusy(false); }
  }
  return <>
    {kind === "mode" ? <Button className={status?.requestedMode === "LIVE" ? "" : "go-live"} disabled={!status || busy || running} onClick={() => open(status?.requestedMode === "LIVE" ? "PAPER" : "LIVE")}>
      {status?.requestedMode === "LIVE" ? "Use PAPER" : <><span aria-hidden="true" className="live-spark" />Go LIVE</>}
    </Button> : <div className="trading-start">
      <Button disabled={!status || busy || running || !withinWindow} onClick={() => open("START")}>Start Trading</Button>
      <p>{running ? "Observation or paper session is running. Real-money execution remains disabled." : !withinWindow ? "Start Trading is available 7:00 AM–6:00 PM IST with a current server connection. You can keep browsing." : "Browsing does not start observation. Confirm START TRADING to begin recording real-data paper research."}</p>
    </div>}
    {action && <dialog ref={dialog} className="panel environment-dialog" aria-labelledby={`confirm-${kind}`} onCancel={e => { if (busy) e.preventDefault(); else setAction(undefined); }}>
      <h2 id={`confirm-${kind}`}>{action === "START" ? "Start Trading" : action === "LIVE" ? "Go LIVE" : "Use PAPER"}</h2>
      {action === "LIVE" ? <><p>Selecting Live requests a mode change only. It does not start trading or place an order.</p><p>Live execution is not available in this release. The Trading page will show the blockers.</p></>
        : action === "PAPER" ? <p>Paper mode uses virtual funds and simulated orders. Selecting it does not start a session.</p>
        : <><p>Selected mode: <strong>{status?.requestedMode}</strong>. {status?.requestedMode === "LIVE" ? status.blocker : "This starts broker-data observation on RELIANCE, ICICIBANK and SBIN, with hypothetical ten-share suggestions and a saved journal. Real funds are untouched. A valid broker token and fresh data are required."}</p><p>Starting is allowed from 7:00 AM up to 6:00 PM IST. This application window does not establish that an exchange session is open.</p></>}
      <form onSubmit={confirm}><label htmlFor={`confirmation-${kind}`}>Type {expected} exactly</label>
        <input id={`confirmation-${kind}`} autoFocus autoComplete="off" value={text} onChange={e => setText(e.target.value)} />
        {error && <p className="error" role="alert">{error}</p>}
        <div className="environment-actions"><Button type="button" variant="outline" disabled={busy} onClick={() => setAction(undefined)}>Cancel</Button><Button disabled={busy || text !== expected || (action === "START" && !withinWindow)}>{busy ? "Checking…" : `Confirm ${expected}`}</Button></div>
      </form>
    </dialog>}
  </>;
}
