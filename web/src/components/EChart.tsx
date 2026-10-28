import type { ECharts } from 'echarts/core';
import { useEffect, useRef } from 'react';
import { echarts, type ChartOption } from '../lib/echarts';

/** Thin wrapper: creates the chart once, resizes it with its container, applies option updates. */
export function EChart({ option, height = 240 }: { option: ChartOption; height?: number }) {
  const el = useRef<HTMLDivElement>(null);
  const chart = useRef<ECharts>();

  useEffect(() => {
    const node = el.current!;
    const c = echarts.init(node);
    chart.current = c;
    const ro = new ResizeObserver(() => c.resize());
    ro.observe(node);
    return () => {
      ro.disconnect();
      c.dispose();
      chart.current = undefined;
    };
  }, []);

  useEffect(() => {
    chart.current?.setOption(option);
  }, [option]);

  return <div ref={el} className="chart" style={{ height }} />;
}
