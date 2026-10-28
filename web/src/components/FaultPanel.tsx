import { useRef, useState, type FormEvent } from 'react';
import type { DeviceDto, FaultName } from '../api/types';
import { useInjectFault } from '../api/queries';
import { useCommandResult, useEffectiveStatus } from '../realtime/hooks';
import { deviceKey } from '../realtime/LiveStore';

const FAULTS: { value: FaultName; label: string; hint: string }[] = [
  { value: 'spike', label: 'Spike', hint: 'A one-sample jump, up or down' },
  { value: 'stuck', label: 'Stuck', hint: 'Output frozen at its last value' },
  { value: 'dropout', label: 'Dropout', hint: 'The sensor stops sending' },
  { value: 'ramp', label: 'Ramp', hint: 'A slow drift away from normal' },
  { value: 'noisy', label: 'Noisy', hint: 'Five times the normal noise' },
];

/** Sends a fault command to the simulated device and shows the device's own answer. */
export function FaultPanel({ device }: { device: DeviceDto }) {
  const inject = useInjectFault(device.site, device.line, device.device);
  const status = useEffectiveStatus(device);
  const result = useCommandResult(deviceKey(device.site, device.line, device.device));

  const [sensor, setSensor] = useState(device.sensors[0]?.sensor ?? '');
  const [fault, setFault] = useState<FaultName>('spike');
  const [duration, setDuration] = useState('');

  // A reply only counts if it arrived after this command was sent.
  const before = useRef(result);
  const [sent, setSent] = useState(false);
  const reply = sent && result !== before.current ? result : undefined;

  const chosenSensor = device.sensors.some((s) => s.sensor === sensor) ? sensor : device.sensors[0]?.sensor ?? '';
  const durationNum = duration.trim() === '' ? undefined : Number(duration);
  const durationInvalid =
    durationNum !== undefined && (!Number.isInteger(durationNum) || durationNum < 1 || durationNum > 1_000_000);
  const hint = FAULTS.find((f) => f.value === fault)?.hint;

  function submit(e: FormEvent) {
    e.preventDefault();
    if (!chosenSensor || durationInvalid) return;
    before.current = result;
    setSent(true);
    inject.mutate({ sensor: chosenSensor, fault, duration: durationNum });
  }

  return (
    <section className="card">
      <header className="card-head">
        <h3>Inject a fault</h3>
      </header>
      <form className="form" onSubmit={submit}>
        <label>
          Sensor
          <select value={chosenSensor} onChange={(e) => setSensor(e.target.value)}>
            {device.sensors.map((s) => (
              <option key={s.seriesId} value={s.sensor}>
                {s.sensor}
              </option>
            ))}
          </select>
        </label>
        <label>
          Fault
          <select value={fault} onChange={(e) => setFault(e.target.value as FaultName)}>
            {FAULTS.map((f) => (
              <option key={f.value} value={f.value}>
                {f.label}
              </option>
            ))}
          </select>
        </label>
        <label>
          Duration (samples)
          <input
            inputMode="numeric"
            placeholder="default"
            value={duration}
            aria-invalid={durationInvalid}
            onChange={(e) => setDuration(e.target.value)}
          />
        </label>
        <button type="submit" className="btn primary" disabled={inject.isPending || durationInvalid || !chosenSensor}>
          {inject.isPending ? 'Sending…' : 'Inject'}
        </button>
      </form>

      <p className="muted small">{hint}.</p>
      {durationInvalid && <p className="error">Duration must be a whole number from 1 to 1,000,000.</p>}
      {status !== 'online' && <p className="warn">This device is {status}; it may not receive the command.</p>}
      {inject.isError && <p className="error" role="alert">{inject.error.message}</p>}
      {inject.isSuccess && !reply && <p className="muted small">Command sent. Waiting for the device to reply…</p>}
      {reply && (
        <p className={reply.ok ? 'ok' : 'error'} role="status">
          {reply.ok ? 'The device accepted the command.' : `The device rejected it: ${reply.error ?? 'unknown reason'}`}
        </p>
      )}
    </section>
  );
}
