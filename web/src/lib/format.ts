export function formatValue(v: number | null | undefined): string {
  if (v === null || v === undefined || !Number.isFinite(v)) return '—';
  const a = Math.abs(v);
  const digits = a >= 1000 ? 0 : a >= 100 ? 1 : 2;
  return v.toFixed(digits);
}

export function timeAgo(iso: string | null | undefined, now: number): string {
  if (!iso) return '—';
  const t = Date.parse(iso);
  if (!Number.isFinite(t)) return '—';
  const s = Math.max(0, Math.round((now - t) / 1000));
  if (s < 5) return 'just now';
  if (s < 60) return `${s}s ago`;
  const m = Math.floor(s / 60);
  if (m < 60) return `${m}m ago`;
  const h = Math.floor(m / 60);
  if (h < 48) return `${h}h ago`;
  return `${Math.floor(h / 24)}d ago`;
}

export function formatBucket(ms: number): string {
  if (ms >= 3_600_000) return `${+(ms / 3_600_000).toFixed(1)} h`;
  if (ms >= 60_000) return `${+(ms / 60_000).toFixed(1)} min`;
  if (ms >= 1000) return `${+(ms / 1000).toFixed(1)} s`;
  return `${ms} ms`;
}

export const SEVERITY_RANK = { info: 0, warning: 1, critical: 2 } as const;
