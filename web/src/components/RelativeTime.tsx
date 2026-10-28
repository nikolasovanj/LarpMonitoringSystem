import { timeAgo } from '../lib/format';
import { useNow } from '../lib/useNow';

export function RelativeTime({ iso }: { iso: string | null | undefined }) {
  const now = useNow();
  if (!iso) return <span className="muted">—</span>;
  return (
    <time dateTime={iso} title={new Date(iso).toLocaleString()}>
      {timeAgo(iso, now)}
    </time>
  );
}
