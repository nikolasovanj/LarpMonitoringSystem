import { NavLink, Outlet } from 'react-router-dom';
import { useAlerts } from '../api/queries';
import { ConnectionIndicator } from './ConnectionIndicator';
import { Toasts } from './Toasts';

export function Layout() {
  const open = useAlerts(true).data?.length ?? 0;

  return (
    <div className="app">
      <header className="topbar">
        <div className="topbar-inner">
          <span className="brand">IoT Monitor</span>
          <nav>
            <NavLink to="/" end>
              Overview
            </NavLink>
            <NavLink to="/alerts">
              Alerts
              {open > 0 && <span className="count">{open}</span>}
            </NavLink>
          </nav>
          <ConnectionIndicator />
        </div>
      </header>
      <main className="main">
        <Outlet />
      </main>
      <Toasts />
    </div>
  );
}
