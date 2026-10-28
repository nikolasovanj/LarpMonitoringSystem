import { loadEnv } from 'vite';
import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';

// In development the browser talks to Vite, and Vite forwards /api and /hubs to the backend.
// That avoids CORS entirely. Point it elsewhere with VITE_PROXY_TARGET, for example
// http://127.0.0.1:8080 for the API container from docker compose.

export default defineConfig(({ mode }) => {
    
    const env = loadEnv(mode, process.cwd(), '');
    const target = env.VITE_PROXY_TARGET || 'http://127.0.0.1:5080';

    return {
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
          // ECharts is the heavy part; keep it in its own cacheable chunk. The function form
          // works on Vite 5 (Rollup) and Vite 8 (Rolldown); the object form only on Rollup.
          manualChunks(id: string) {
            if (!id.includes('node_modules')) return undefined;
            return id.includes('/echarts/') || id.includes('/zrender/') ? 'echarts' : 'vendor';
          },
        },
      },
    },
    // Unit tests only; the Playwright specs in e2e/ are run by `npm run e2e`.
    test: { environment: 'node', include: ['src/**/*.test.ts'] },
  };
});
