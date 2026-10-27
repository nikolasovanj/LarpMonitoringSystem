import { useMemo } from 'react';
import type { HistoryPoint } from '../api/types';
import { baseOption, COLORS } from '../lib/chartTheme';
import { EChart } from './EChart';

/** Average line with thin min and max lines, from the API's bucketed history. */
export function HistoryChart({ points, unit }: { points: HistoryPoint[]; unit: string | null }) {
  const option = useMemo(() => {
    const line = (name: string, pick: (p: HistoryPoint) => number, color: string, width: number, opacity: number) => ({
      name,
      type: 'line',
      showSymbol: false,
      lineStyle: { width, color, opacity },
      itemStyle: { color },
      data: points.map((p) => [Date.parse(p.time), pick(p)]),
    });
    return {
      ...baseOption(unit),
      series: [
        line('max', (p) => p.max, COLORS.band, 1, 0.45),
        line('min', (p) => p.min, COLORS.band, 1, 0.45),
        line('avg', (p) => p.avg, COLORS.accent, 1.75, 1),
      ],
    };
  }, [points, unit]);

  return <EChart option={option} height={280} />;
}
