interface Props {
  label: string;
  value: string | number;
  tone?: 'ok' | 'warn' | 'crit' | 'neutral';
}

export function StatCard({ label, value, tone = 'neutral' }: Props) {
  return (
    <div className={`stat stat-${tone}`}>
      <span className="stat-value mono">{value}</span>
      <span className="stat-label">{label}</span>
    </div>
  );
}
