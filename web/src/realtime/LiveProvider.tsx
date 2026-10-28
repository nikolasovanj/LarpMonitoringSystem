import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useState, type ReactNode } from 'react';
import { LiveContext } from './LiveContext';
import { LiveStore } from './LiveStore';

/** Owns the single SignalR connection and ties it to the query cache. */
export function LiveProvider({ children }: { children: ReactNode }) {
  const qc = useQueryClient();
  const [store] = useState(() => new LiveStore());

  useEffect(() => {
    store.onReconnected = () => void qc.invalidateQueries();
    store.onAlert = () => void qc.invalidateQueries({ queryKey: ['alerts'] });
    store.start();
    return () => {
      store.stop();
      store.onReconnected = null;
      store.onAlert = null;
    };
  }, [store, qc]);

  return <LiveContext.Provider value={store}>{children}</LiveContext.Provider>;
}
