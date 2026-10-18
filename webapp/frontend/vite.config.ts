import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// The build goes straight into the API's wwwroot, so one App Service serves the screens and the API.
export default defineConfig({
  plugins: [react()],
  build: { outDir: '../src/Enppi.Workload.Api/wwwroot', emptyOutDir: true, sourcemap: false },
  server: { port: 5173, proxy: { '/api': 'http://127.0.0.1:5080', '/health': 'http://127.0.0.1:5080' } },
});
