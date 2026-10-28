import type { AlertDto, DeviceDto, HistoryDto, InjectFaultRequest } from './types';

/** Empty = same origin (Vite proxy in dev, nginx in the container). */
export const API_BASE: string = import.meta.env.VITE_API_URL ?? '';

export class ApiError extends Error {
  constructor(
    public readonly status: number,
    message: string,
  ) {
    super(message);
    this.name = 'ApiError';
  }
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  let res: Response;
  try {
    res = await fetch(`${API_BASE}${path}`, {
      ...init,
      headers: {
        Accept: 'application/json',
        ...(init?.body ? { 'Content-Type': 'application/json' } : {}),
        ...init?.headers,
      },
    });
  } catch {
    throw new ApiError(0, 'Cannot reach the API. Is the backend running?');
  }

  if (!res.ok) {
    let message = `${res.status} ${res.statusText}`;
    try {
      // ASP.NET problem details: { title, detail, status }
      const problem = (await res.json()) as { detail?: string; title?: string };
      message = problem.detail ?? problem.title ?? message;
    } catch {
      /* body was not JSON */
    }
    throw new ApiError(res.status, message);
  }

  const text = await res.text(); // 202 and 204 have no body
  return (text ? JSON.parse(text) : undefined) as T;
}

export const getDevices = () => request<DeviceDto[]>('/api/devices');

export const getAlerts = (openOnly: boolean) =>
  request<AlertDto[]>(`/api/alerts?openOnly=${openOnly}`);

export const ackAlert = (id: number, by: string) =>
  request<void>(`/api/alerts/${id}/ack`, { method: 'POST', body: JSON.stringify({ by }) });

export function getHistory(seriesId: number, from: Date, to: Date, maxPoints: number) {
  const q = new URLSearchParams({
    from: from.toISOString(),
    to: to.toISOString(),
    maxPoints: String(maxPoints),
  });
  return request<HistoryDto>(`/api/series/${seriesId}/history?${q}`);
}

/** Fire-and-forget: 202 means the command was published. The device's answer arrives on the hub. */
export function injectFault(site: string, line: string, device: string, body: InjectFaultRequest) {
  const p = [site, line, device].map(encodeURIComponent).join('/');
  return request<void>(`/api/devices/${p}/faults`, { method: 'POST', body: JSON.stringify(body) });
}
