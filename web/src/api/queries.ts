import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { rangeMs, type RangeKey } from '../lib/ranges';
import { ackAlert, getAlerts, getDevices, getHistory, injectFault } from './client';
import type { InjectFaultRequest } from './types';

export const useDevices = () =>
  useQuery({ queryKey: ['devices'], queryFn: getDevices, refetchInterval: 15_000 });

export const useAlerts = (openOnly: boolean) =>
  useQuery({
    queryKey: ['alerts', openOnly],
    queryFn: () => getAlerts(openOnly),
    refetchInterval: 30_000,
  });

export function useHistory(seriesId: number | undefined, range: RangeKey) {
  return useQuery({
    queryKey: ['history', seriesId, range],
    enabled: seriesId !== undefined,
    queryFn: () => {
      const to = new Date();
      return getHistory(seriesId!, new Date(to.getTime() - rangeMs(range)), to, 600);
    },
    refetchInterval: 30_000,
    placeholderData: keepPreviousData, // no flash of an empty chart when switching range
  });
}

export function useAckAlert() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: number) => ackAlert(id, 'dashboard'),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['alerts'] }),
  });
}

export function useInjectFault(site: string, line: string, device: string) {
  return useMutation({
    mutationFn: (body: InjectFaultRequest) => injectFault(site, line, device, body),
  });
}
