import { createContext } from 'react';
import type { LiveStore } from './LiveStore';

export const LiveContext = createContext<LiveStore | null>(null);
