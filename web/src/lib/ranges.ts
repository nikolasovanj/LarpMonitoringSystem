export const HISTORY_RANGES = [
  { key: '15m', label: '15 min', ms: 15 * 60_000 },
  { key: '1h', label: '1 hour', ms: 60 * 60_000 },
  { key: '6h', label: '6 hours', ms: 6 * 60 * 60_000 },
  { key: '24h', label: '24 hours', ms: 24 * 60 * 60_000 },
  { key: '7d', label: '7 days', ms: 7 * 24 * 60 * 60_000 },
] as const;

export type RangeKey = (typeof HISTORY_RANGES)[number]['key'];

export const rangeMs = (key: RangeKey) => HISTORY_RANGES.find((r) => r.key === key)!.ms;
