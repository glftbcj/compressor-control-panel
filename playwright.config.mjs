import { defineConfig } from '@playwright/test';
const dotnet = process.env.HVACR_DOTNET || 'dotnet';
export default defineConfig({
  testDir: './tests/ui',
  fullyParallel: false,
  workers: 1,
  retries: 0,
  timeout: 30_000,
  reporter: [['list']],
  outputDir: 'artifacts/browser-tests',
  use: {
    baseURL: 'http://127.0.0.1:43121',
    viewport: { width: 1360, height: 1020 },
    reducedMotion: 'reduce',
    launchOptions: process.env.HVACR_BROWSER ? { executablePath: process.env.HVACR_BROWSER } : {},
    trace: 'retain-on-failure'
  },
  webServer: {
    command: '"' + dotnet + '" run --project src/Hvacr.Server -c Release --no-build',
    url: 'http://127.0.0.1:43121/api/health',
    reuseExistingServer: process.env.HVACR_TEST_EXTERNAL === 'true',
    env: {
      HVACR_SIMULATION: 'true', HVACR_READ_ONLY: 'false',
      HVACR_DATA_DIR: process.cwd() + '/.test-data/ui',
      HVACR_SETTINGS: process.cwd() + '/.test-data/no-settings.json',
      HVACR_ACCESS_TOKEN: '', PORT: '43121', HOST: '127.0.0.1',
      Logging__LogLevel__Default: 'Warning'
    }
  }
});
