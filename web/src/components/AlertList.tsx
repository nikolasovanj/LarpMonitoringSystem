import { Link } from 'react-router-dom';
import type { AlertDto } from '../api/types';
import { useAckAlert } from '../api/queries';
import { RelativeTime } from './RelativeTime';
import { SeverityBadge } from './SeverityBadge';

function alertState(a: AlertDto) {
  if (a.resolvedAt) return 'resolved';
  if (a.acknowledgedAt) return 'acknowledged';
  return 'open';
}

export function AlertList({ alerts, showDevice = true }: { alerts: AlertDto[]; showDevice?: boolean }) {
  const ack = useAckAlert();

  if (alerts.length === 0) return <p className="muted empty">No alerts.</p>;

  return (
    <div className="table-wrap">
      <table className="table">
        <thead>
          <tr>
            <th>Severity</th>
            <th>Alert</th>
            {showDevice && <th>Device</th>}
            <th>Opened</th>
            <th>State</th>
            <th />
          </tr>
        </thead>
        <tbody>
          {alerts.map((a) => {
            const state = alertState(a);
            const pending = ack.isPending && ack.variables === a.id;
            return (
              <tr key={a.id} className={state === 'resolved' ? 'row-dim' : ''}>
                <td>
                  <SeverityBadge severity={a.severity} />
                </td>
                <td>{a.message}</td>
                {showDevice && (
                  <td>
                    {a.site && a.line && a.device ? (
                      <Link to={`/devices/${encodeURIComponent(a.site)}/${encodeURIComponent(a.line)}/${encodeURIComponent(a.device)}`}>
                        {a.device}
                      </Link>
                    ) : (
                      <span className="muted">—</span>
                    )}
                    {a.sensor && <span className="muted"> · {a.sensor}</span>}
                  </td>
                )}
                <td>
                  <RelativeTime iso={a.openedAt} />
                </td>
                <td>
                  <span className={`state state-${state}`}>{state}</span>
                </td>
                <td className="right">
                  {state === 'open' && (
                    <button type="button" className="btn small" disabled={pending} onClick={() => ack.mutate(a.id)}>
                      {pending ? '…' : 'Acknowledge'}
                    </button>
                  )}
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>
      {ack.isError && <p className="error">{ack.error.message}</p>}
    </div>
  );
}
