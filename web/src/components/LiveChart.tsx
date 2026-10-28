import { useMemo } from 'react';
import { baseOption, COLORS } from '../lib/chartTheme';
import { useLiveBuffer } from '../realtime/hooks';
import { EChart } from './EChart';

/** Streaming chart of the in-memory buffer for one series. */
export function LiveChart({ seriesKey, unit }: { seriesKey: string; unit: string | null }) {
  const points = useLiveBuffer(seriesKey);

  const option = useMemo(
    () => ({
      ...baseOption(unit),
      series: [
        {
          type: 'line',
          showSymbol: false,
          sampling: 'lttb',
          lineStyle: { width: 1.5, color: COLORS.accent },
          itemStyle: { color: COLORS.accent },
          data: points.map((p) => [p.t, p.v]),
        },
      ],
    }),
    [points, unit],
  );

  return points.length === 0 ? (
    <p className="muted empty chart-empty">Waiting for live data…</p>
  ) : (
    <EChart option={option} height={200} />
  );
}
