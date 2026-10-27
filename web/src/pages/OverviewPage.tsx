import { Link } from 'react-router-dom';
import type { DeviceDto } from '../api/types';
import { useAlerts, useDevices } from '../api/queries';
import { AlertList } from '../components/AlertList';
import { DeviceCard } from '../components/DeviceCard';
import { ErrorMessage, Loading } from '../components/QueryState';
import { StatCard } from '../components/StatCard';
import { useStatusMap } from '../realtime/hooks';
import { deviceKey } from '../realtime/LiveStore';

function groupByLine(devices: DeviceDto[]) {
  const groups = new Map<string, DeviceDto[]>();
  for (const d of devices) {
    const key = `${d.site} · ${d.line}`;
    groups.set(key, [...(groups.get(key) ?? []), d]);
  }
  return [...groups.entries()].sort(([a], [b]) => a.localeCompare(b));
}

export function OverviewPage() {
  const devices = useDevices();
  const alerts = useAlerts(true);
  const live = useStatusMap();

  if (devices.isPending) return <Loading label="Loading devices…" />;
  if (devices.isError) return <ErrorMessage error={devices.error} />;

  const list = devices.data;
  const statusOf = (d: DeviceDto) => live.get(deviceKey(d.site, d.line, d.device))?.status ?? d.status;
  const online = list.filter((d) => statusOf(d) === 'online').length;
  const offline = list.filter((d) => statusOf(d) === 'offline').length;
  const open = alerts.data ?? [];
  const critical = open.filter((a) => a.severity === 'critical').length;

  return (
    <>
      <div className="page-head">
        <h1>Plant overview</h1>
      </div>

      <div className="stats">
        <StatCard label="Devices" value={list.length} />
        <StatCard label="Online" value={online} tone="ok" />
        <StatCard label="Offline" value={offline} tone={offline > 0 ? 'warn' : 'neutral'} />
        <StatCard label="Open alerts" value={open.length} tone={critical > 0 ? 'crit' : open.length > 0 ? 'warn' : 'neutral'} />
      </div>

      {open.length > 0 && (
        <section className="card">
          <header className="card-head">
            <h2>Open alerts</h2>
            <Link to="/alerts">View all</Link>
          </header>
          <AlertList alerts={open.slice(0, 5)} />
        </section>
      )}

      {list.length === 0 ? (
        <p className="muted empty">
          No devices yet. Start a simulator, for example <code>docker compose up sim-pump01</code>, and it will appear here.
        </p>
      ) : (
        groupByLine(list).map(([group, devs]) => (
          <section key={group}>
            <h2 className="group-title">{group}</h2>
            <div className="grid">
              {devs.map((d) => (
                <DeviceCard key={`${d.site}/${d.line}/${d.device}`} device={d} />
              ))}
            </div>
          </section>
        ))
      )}
    </>
  );
}
