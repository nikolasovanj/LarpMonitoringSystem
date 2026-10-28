import { useState } from 'react';
import { useAlerts } from '../api/queries';
import { AlertList } from '../components/AlertList';
import { ErrorMessage, Loading } from '../components/QueryState';

export function AlertsPage() {
  const [openOnly, setOpenOnly] = useState(true);
  const alerts = useAlerts(openOnly);

  return (
    <>
      <div className="page-head">
        <h1>Alerts</h1>
        <div className="segmented" role="group" aria-label="Filter">
          <button type="button" className={openOnly ? 'active' : ''} onClick={() => setOpenOnly(true)}>
            Open
          </button>
          <button type="button" className={!openOnly ? 'active' : ''} onClick={() => setOpenOnly(false)}>
            All (latest 200)
          </button>
        </div>
      </div>

      <section className="card">
        {alerts.isPending && <Loading />}
        {alerts.isError && <ErrorMessage error={alerts.error} />}
        {alerts.data && <AlertList alerts={alerts.data} />}
      </section>
    </>
  );
}
