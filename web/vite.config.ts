import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';

// In development the browser talks to Vite, and Vite forwards /api and /hubs to the backend.
// That avoids CORS entirely. Point it elsewhere with VITE_PROXY_TARGET, for example
// http://localhost:8080 for the API container from docker compose.
const target = process.env.VITE_PROXY_TARGET ?? 'http://localhost:5080';

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      '/api': { target, changeOrigin: true },
      '/hubs': { target, changeOrigin: true, ws: true },
    },
  },
  build: {
    rollupOptions: {
      output: {
        // ECharts is the heavy part; keep it in its own cacheable chunk.
        manualChunks: {
          echarts: ['echarts'],
          vendor: ['react', 'react-dom', 'react-router-dom', '@tanstack/react-query', '@microsoft/signalr'],
        },
      },
    },
  },
  test: { environment: 'node' },
});
