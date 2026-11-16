import { useEffect, useRef } from "react";
import * as echarts from "echarts/core";
import { CandlestickChart } from "echarts/charts";
import { GridComponent, TooltipComponent, DataZoomComponent } from "echarts/components";
import { CanvasRenderer } from "echarts/renderers";
import { time } from "../types";
echarts.use([CandlestickChart, GridComponent, TooltipComponent, DataZoomComponent, CanvasRenderer]);
export type Evidence = { candles: { timestampUtc: string; open: number; high: number; low: number; close: number; volume: number }[]; ema20: number | null; rsi14: number | null; atr14: number | null; relativeVolume: number | null; vwap: number | null; breakoutLevel: number | null; news: string; context: string };
export function CandleEvidence({ evidence }: { evidence: Evidence }) {
  const ref = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!ref.current || !evidence.candles.length) return;
    const chart = echarts.init(ref.current);
    chart.setOption({ animation: false, tooltip: { trigger: "axis", renderMode: "richText" }, grid: { left: 65, right: 24, top: 20, bottom: 70 }, xAxis: { type: "category", data: evidence.candles.map(c => time(c.timestampUtc)) }, yAxis: { scale: true }, dataZoom: [{ type: "inside" }, { type: "slider", bottom: 10 }], series: [{ name: "Completed broker candles", type: "candlestick", data: evidence.candles.map(c => [c.open, c.close, c.low, c.high]), itemStyle: { color: "#147d69", color0: "#ce6a65", borderColor: "#147d69", borderColor0: "#ce6a65" } }] });
    const observer = new ResizeObserver(() => chart.resize()); observer.observe(ref.current);
    return () => { observer.disconnect(); chart.dispose(); };
  }, [evidence]);
  return <><p className="footnote">Completed broker candles, opening times in IST. Evidence is frozen when a suggestion is recorded.</p><div ref={ref} role="img" aria-label="Completed broker candlestick chart" className="research-chart" /><div className="research-metrics">{Object.entries({ EMA20: evidence.ema20, RSI14: evidence.rsi14, ATR14: evidence.atr14, "Relative volume": evidence.relativeVolume, VWAP: evidence.vwap, "Breakout level": evidence.breakoutLevel }).map(([label, value]) => <span key={label}>{label} <strong>{value == null ? "Unavailable" : value.toFixed(2)}</strong></span>)}</div><p className="footnote">{evidence.news} {evidence.context}</p></>;
}
