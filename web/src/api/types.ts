// Mirrors IotMonitor.Contracts (System.Text.Json camelCase). If the API changes, regenerate or
// compare against `npx openapi-typescript http://localhost:5080/swagger/v1/swagger.json`.

export type DeviceStatus = 'online' | 'offline' | 'unknown';
export type Severity = 'info' | 'warning' | 'critical';
export type FaultName = 'spike' | 'stuck' | 'dropout' | 'ramp' | 'noisy';

export interface SensorDto {
  seriesId: number;
  sensor: string;
  unit: string | null;
  latestValue: number | null;
  latestTime: string | null;
}

export interface DeviceDto {
  site: string;
  line: string;
  device: string;
  status: DeviceStatus;
  statusSince: string | null;
  lastSeen: string | null;
  sensors: SensorDto[];
}

export interface HistoryPoint {
  time: string;
  avg: number;
  min: number;
  max: number;
  samples: number;
}

export interface HistoryDto {
  seriesId: number;
  source: 'Raw' | 'Rollup1m';
  bucketMs: number;
  points: HistoryPoint[];
}

export interface AlertDto {
  id: number;
  ruleId: number | null;
  seriesId: number | null;
  site: string | null;
  line: string | null;
  device: string | null;
  sensor: string | null;
  severity: Severity;
  message: string;
  value: number | null;
  openedAt: string;
  resolvedAt: string | null;
  acknowledgedAt: string | null;
  acknowledgedBy: string | null;
}

export interface InjectFaultRequest {
  sensor: string;
  fault: FaultName;
  duration?: number;
}

// ---- live (SignalR) payloads ----

export interface LiveReading {
  site: string;
  line: string;
  device: string;
  sensor: string;
  value: number;
  unit: string | null;
  time: string;
}

export interface DeviceStatusChanged {
  site: string;
  line: string;
  device: string;
  status: DeviceStatus;
  time: string;
}

export interface CommandResultDto {
  site: string;
  line: string;
  device: string;
  ok: boolean;
  error: string | null;
  time: string;
}

export interface AlertEvent {
  type: 'opened' | 'resolved';
  alertId: number;
  severity: Severity;
  message: string;
  site: string | null;
  line: string | null;
  device: string | null;
  sensor: string | null;
  value: number | null;
  time: string;
}
