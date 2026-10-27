import { useContext, useSyncExternalStore } from 'react';
import type { DeviceStatus } from '../api/types';
import { LiveContext } from './LiveContext';
import { deviceKey, type LiveStore } from './LiveStore';

export function useLiveStore(): LiveStore {
  const store = useContext(LiveContext);
  if (!store) throw new Error('useLiveStore must be used inside <LiveProvider>');
  return store;
}

export function useConnectionState() {
  const s = useLiveStore();
  return useSyncExternalStore(s.subscribe, s.getConnectionState);
}

/** Recent points of one series (see seriesKey). Same array reference until it changes. */
export function useLiveBuffer(key: string) {
  const s = useLiveStore();
  return useSyncExternalStore(s.subscribe, () => s.getBuffer(key));
}

export function useLatest(key: string) {
  const s = useLiveStore();
  return useSyncExternalStore(s.subscribe, () => s.getLatest(key));
}

export function useStatusMap() {
  const s = useLiveStore();
  return useSyncExternalStore(s.subscribe, s.getStatuses);
}

/** The pushed status wins over the fetched one: it can only be newer. */
export function useEffectiveStatus(d: {
  site: string;
  line: string;
  device: string;
  status: DeviceStatus;
}): DeviceStatus {
  const live = useStatusMap().get(deviceKey(d.site, d.line, d.device));
  return live?.status ?? d.status;
}

export function useCommandResult(key: string) {
  const s = useLiveStore();
  return useSyncExternalStore(s.subscribe, () => s.getResult(key));
}

export function useLiveEvents() {
  const s = useLiveStore();
  return useSyncExternalStore(s.subscribe, s.getEvents);
}
