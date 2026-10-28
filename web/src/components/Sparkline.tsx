import type { LivePoint } from '../realtime/LiveStore';

interface Props {
  points: readonly LivePoint[];
  width?: number;
  height?: number;
  /** How many of the newest points to draw. */
  last?: number;
}

export function Sparkline({ points, width = 120, height = 32, last = 60 }: Props) {
  const pts = points.slice(-last);
  if (pts.length < 2) return <svg className="spark" width={width} height={height} aria-hidden />;

  let min = Infinity;
  let max = -Infinity;
  for (const p of pts) {
    if (p.v < min) min = p.v;
    if (p.v > max) max = p.v;
  }
  const span = max - min || 1;
  const stepX = width / (pts.length - 1);
  const d = pts
    .map((p, i) => `${(i * stepX).toFixed(1)},${(height - 2 - ((p.v - min) / span) * (height - 4)).toFixed(1)}`)
    .join(' ');

  return (
    <svg className="spark" width={width} height={height} viewBox={`0 0 ${width} ${height}`} aria-hidden>
      <polyline points={d} fill="none" strokeWidth="1.5" strokeLinejoin="round" />
    </svg>
  );
}
