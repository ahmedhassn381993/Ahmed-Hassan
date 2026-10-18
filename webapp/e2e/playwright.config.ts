import { defineConfig } from '@playwright/test';

// Runs against a local instance with the demonstration data (Development sign-in):
//   cd src/Enppi.Workload.Api && ASPNETCORE_ENVIRONMENT=Development dotnet run --no-launch-profile --urls http://127.0.0.1:5080
export default defineConfig({
  testDir: './tests',
  timeout: 60_000,
  workers: 1,
  reporter: [['list']],
  use: {
    baseURL: process.env.BASE_URL ?? 'http://127.0.0.1:5080',
    viewport: { width: 1440, height: 900 },
    screenshot: 'only-on-failure',
    launchOptions: process.env.CHROMIUM_PATH ? { executablePath: process.env.CHROMIUM_PATH } : {},
  },
  webServer: {
    command: 'dotnet run --no-launch-profile --urls http://127.0.0.1:5080',
    cwd: '../src/Enppi.Workload.Api',
    url: 'http://127.0.0.1:5080/health',
    reuseExistingServer: true,
    timeout: 120_000,
    env: { ASPNETCORE_ENVIRONMENT: 'Development' },
  },
});
