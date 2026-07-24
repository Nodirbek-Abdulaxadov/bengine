/// <reference types="vitest/config" />
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  server: {
    // Listen on all interfaces so a reverse proxy can reach the dev server.
    host: true,
    // Hosts allowed to reach the dev server (needed when served behind a
    // reverse proxy / custom domain). Add more entries as needed.
    allowedHosts: ['db-designer.tulki.uz'],
    // Proxy generation requests to the Bengine.Api backend during development.
    proxy: {
      '/api': 'http://localhost:5099',
    },
  },
  preview: {
    host: true,
    allowedHosts: ['db-designer.tulki.uz'],
  },
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: './src/test/setup.ts',
  },
});
