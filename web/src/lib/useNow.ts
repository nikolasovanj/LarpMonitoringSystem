import { useSyncExternalStore } from 'react';

// One shared 1 s ticker for every "5s ago" label, instead of a timer per component.
let now = Date.now();
const subs = new Set<() => void>();
let timer: number | undefined;

function subscribe(cb: () => void) {
  now = Date.now();
  subs.add(cb);
  if (timer === undefined) {
    timer = window.setInterval(() => {
      now = Date.now();
      subs.forEach((s) => s());
    }, 1000);
  }
  return () => {
    subs.delete(cb);
    if (subs.size === 0 && timer !== undefined) {
      window.clearInterval(timer);
      timer = undefined;
    }
  };
}

export const useNow = () => useSyncExternalStore(subscribe, () => now);
