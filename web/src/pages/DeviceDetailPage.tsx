import { useParams } from 'react-router-dom';
import { useAlerts, useDevices } from '../api/queries';
import { AlertList } from '../components/AlertList';
import { DeviceHeader } from '../components/DeviceHeader';
import { FaultPanel } from '../components/FaultPanel';
import { HistoryPanel } from '../components/HistoryPanel';
import { ErrorMessage, Loading } from '../components/QueryState';
import { SensorChartCard } from '../components/SensorChartCard';
import { NotFoundPage } from './NotFoundPage';

export function DeviceDetailPage() {
  const { site, line, device: id } = useParams();
  const devices = useDevices();
  const alerts = useAlerts(false);

  if (devices.isPending) return <Loading label="Loading device…" />;
  if (devices.isError) return <ErrorMessage error={devices.error} />;

  const device = devices.data.find((d) => d.site === site && d.line === line && d.device === id);
  if (!device) return <NotFoundPage what={`Device "${id}"`} />;

  const deviceAlerts = (alerts.data ?? [])
    .filter((a) => a.site === device.site && a.line === device.line && a.device === device.device)
    .slice(0, 10);

  return (
    <>
      <DeviceHeader device={device} />

      <div className="grid wide">
        {device.sensors.map((s) => (
          <SensorChartCard key={s.seriesId} device={device} sensor={s} />
        ))}
      </div>

      <HistoryPanel device={device} />
      <FaultPanel device={device} />

      <section className="card">
        <header className="card-head">
          <h2>Recent alerts</h2>
        </header>
        <AlertList alerts={deviceAlerts} showDevice={false} />
      </section>
    </>
  );
}
