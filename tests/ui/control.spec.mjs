import { test, expect } from '@playwright/test';
import { readFile } from 'node:fs/promises';

let deviceId;
test.beforeEach(async ({ request }) => {
  deviceId = 'ui-test-' + Math.random().toString(16).slice(2);
  const config = await (await request.get('/api/config')).json();
  const headers = { 'X-Hvacr-Session': config.sessionToken };
  await request.put('/api/mode', { headers, data: { readOnly: false, confirmEnable: true } });
  await request.put('/api/preferences/appearance', { headers, data: { theme: 'system', accent: '#147c6e' } });
  await request.put('/api/preferences/devices', { headers, data: { devices: [{ deviceId }] } });
  await request.post('/api/control', { headers, data: { deviceId, action: 'get_data' } });
});
async function ready(page) {
  await page.goto('/');
  await expect(page.locator('#currentSetTemp')).toHaveText('23.0');
  await expect(page.locator('#tempUpBtn')).toBeEnabled();
  await expect(page.locator('#appearanceBtn')).toBeEnabled();
}
function writes(page) {
  const sent = [];
  page.on('request', request => {
    if (request.url().endsWith('/api/control') && request.method() === 'POST') {
      const body = request.postDataJSON();
      if (body.action !== 'get_data') sent.push(body);
    }
  });
  return sent;
}
test('Dashboard renders telemetry and visible chart without JavaScript errors', async ({ page }) => {
  const errors = [];
  page.on('pageerror', e => errors.push(e.message));
  await ready(page);
  await expect(page.locator('#simulationBanner')).toBeVisible();
  await expect(page.locator('#currentPumpSpeed')).toHaveText('5');
  await expect(page.locator('#frequency')).toHaveText('35');
  await expect(page.locator('#temperatureChart')).toBeVisible();
  await expect(page.locator('#chartEnd')).toHaveAttribute('cy', /\d/);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await page.screenshot({ path: 'artifacts/ui-desktop.png', fullPage: true });
  expect(errors).toEqual([]);
});
test('Editing a temperature draft sends no command until apply', async ({ page }) => {
  await ready(page);
  const sent = writes(page);
  await page.locator('#tempUpBtn').click();
  await expect(page.locator('#temperatureInput')).toHaveValue('24');
  expect(sent).toEqual([]);
  await expect(page.locator('#currentSetTemp')).toHaveText('23.0');
});
test('Temperature applies without a dialog and completes under one second with no interval', async ({ page }) => {
  await ready(page);
  const sent = writes(page);
  await page.locator('#temperatureInput').fill('10.5');
  const started = Date.now();
  await page.locator('#applyTempBtn').click();
  await expect(page.locator('#currentSetTemp')).toHaveText('10.5', { timeout: 1000 });
  expect(Date.now() - started).toBeLessThan(1000);
  await expect(page.locator('#confirmDialog')).not.toBeVisible();
  expect(sent).toHaveLength(1);
  expect(sent[0]).toMatchObject({ deviceId, action: 'setTemperature', value: 10.5, expectedValue: 23 });
  expect(sent[0].commandId).toMatch(/^[0-9a-f-]{36}$/);
  await expect(page.locator('#temperatureInput')).toBeEnabled({ timeout: 1000 });
  await page.locator('#temperatureInput').fill('11.5');
  await page.locator('#applyTempBtn').click();
  await expect(page.locator('#currentSetTemp')).toHaveText('11.5', { timeout: 1000 });
  expect(sent).toHaveLength(2);
});
test('Pump slider sends once on apply without another confirmation', async ({ page }) => {
  await ready(page);
  const sent = writes(page);
  await page.locator('#pumpSlider').fill('9');
  await expect(page.locator('#pumpTarget')).toHaveText('9');
  expect(sent).toHaveLength(0);
  await page.locator('#applyPumpBtn').click();
  await expect(page.locator('#confirmDialog')).not.toBeVisible();
  await expect(page.locator('#currentPumpSpeed')).toHaveText('9');
  expect(sent).toHaveLength(1);
  expect(sent[0]).toMatchObject({ action: 'setWindSpeed', value: 9, expectedValue: 5 });
});
test('A later conflicting cloud temperature leaves the applied target and input intact', async ({ page }) => {
  let conflict = false;
  await page.route('**/api/events', route => route.abort());
  await page.route('**/api/status', async route => {
    const response = await route.fetch(); const json = await response.json();
    if (conflict) for (const device of json.devices) device.payload.set_temp = 14;
    await route.fulfill({ response, json });
  });
  await ready(page);
  const sent = writes(page);
  await page.locator('#temperatureInput').fill('10');
  await page.locator('#applyTempBtn').click();
  await expect(page.locator('#temperatureReadback')).toContainText('已收到一致回报');
  conflict = true;
  await page.locator('#refreshBtn').click();
  await expect(page.locator('#temperatureReadback')).toContainText('云端回报 14.0℃');
  await expect(page.locator('#temperatureReadback')).toHaveAttribute('data-conflict', 'true');
  await expect(page.locator('#currentSetTemp')).toHaveText('10.0');
  await expect(page.locator('#temperatureInput')).toHaveValue('10');
  await page.locator('#pumpSlider').fill('6');
  await page.locator('#applyPumpBtn').click();
  await expect(page.locator('#currentPumpSpeed')).toHaveText('6');
  await page.reload();
  await expect(page.locator('#currentSetTemp')).toHaveText('10.0');
  await expect(page.locator('#temperatureInput')).toHaveValue('10');
  await expect(page.locator('#temperatureReadback')).toContainText('与本机目标不同');
  expect(sent.map(command => command.action)).toEqual(['setTemperature', 'setWindSpeed']);
});
test('Power action requires checkbox confirmation and never happens on page load', async ({ page }) => {
  const sent = writes(page);
  await ready(page);
  expect(sent).toEqual([]);
  await page.locator('.power-details summary').click();
  await page.locator('#stopBtn').click();
  await expect(page.locator('#confirmSendBtn')).toBeDisabled();
  await page.locator('#powerConfirmCheck').check();
  await expect(page.locator('#confirmSendBtn')).toBeEnabled();
  await page.locator('#confirmSendBtn').click();
  await expect(page.locator('#powerState')).toHaveText('压缩机已停止');
  expect(sent).toHaveLength(1);
  expect(sent[0]).toMatchObject({ action: 'stop', confirmPower: true, expectedValue: true });
});
test('Connected read only mode can be enabled and disabled inside the page without a command', async ({ page, request }) => {
  const config = await (await request.get('/api/config')).json();
  await request.put('/api/mode', { headers: { 'X-Hvacr-Session': config.sessionToken }, data: { readOnly: true } });
  const sent = writes(page);
  await page.goto('/');
  await expect(page.locator('#modeBadge')).toHaveText('只读监控');
  for (const id of ['temperatureInput', 'tempUpBtn', 'tempDownBtn', 'pumpSlider', 'applyPumpBtn', 'startBtn', 'stopBtn'])
    await expect(page.locator('#' + id)).toBeDisabled();
  await expect(page.locator('#connectionLabel')).toHaveText('通信已连接');
  await page.locator('#modeToggleBtn').click();
  await expect(page.locator('dialog[open]')).toHaveCount(0);
  await expect(page.locator('#temperatureInput')).toBeEnabled();
  await expect(page.locator('#modeBadge')).toHaveText('控制已启用');
  await page.reload();
  await expect(page.locator('#pumpSlider')).toBeEnabled();
  await page.locator('#modeToggleBtn').click();
  await expect(page.locator('#temperatureInput')).toBeDisabled();
  expect(sent).toHaveLength(0);
});
test('Stale target values remain disabled when other fields are fresh', async ({ page }) => {
  await page.route('**/api/events', route => route.abort());
  await page.route('**/api/status', async route => {
    const response = await route.fetch(); const json = await response.json();
    for (const device of json.devices) {
      device.fieldSeenAt.set_temp = json.serverTime - 60_000;
      device.fieldSeenAt.wind_speed_set = json.serverTime - 60_000;
    }
    await route.fulfill({ response, json });
  });
  await page.goto('/');
  await expect(page.locator('#currentSetTemp')).toHaveText('23.0');
  await expect(page.locator('#tempUpBtn')).toBeDisabled();
  await expect(page.locator('#pumpSlider')).toBeDisabled();
  await expect(page.locator('#controlHint')).toContainText('新鲜回读');
});
test('Null telemetry displays a dash, never zero', async ({ page }) => {
  await page.route('**/api/events', route => route.abort());
  await page.route('**/api/status', async route => {
    const response = await route.fetch(); const json = await response.json();
    for (const device of json.devices) {
      device.payload.sj_temp = null; device.payload.voltage = null; device.payload.wind_speed_set = null;
    }
    await route.fulfill({ response, json });
  });
  await page.goto('/');
  await expect(page.locator('#currentTemp')).toHaveText('—');
  await expect(page.locator('#voltage')).toHaveText('—');
  await expect(page.locator('#currentPumpSpeed')).toHaveText('—');
  await expect(page.locator('#pumpSlider')).toBeDisabled();
});
test('Adding a device selects it; deleting it does not restore it on reload', async ({ page }) => {
  await ready(page);
  const next = 'second-ui-' + Math.random().toString(16).slice(2);
  await page.locator('#openAddBtn').click();
  await page.locator('#deviceIdInput').fill(next);
  await page.locator('#addDeviceBtn').click();
  await expect(page.locator('#selectedDeviceName')).toHaveText(next);
  await expect(page.locator('#currentSetTemp')).toHaveText('23.0');
  page.once('dialog', dialog => dialog.accept());
  await page.locator('#deleteDeviceBtn').click();
  await expect(page.locator('#selectedDeviceName')).toHaveText(deviceId);
  await page.reload();
  await expect(page.locator('#deviceSelect option')).toHaveCount(1);
  await expect(page.locator('#selectedDeviceName')).toHaveText(deviceId);
});
test('Switching devices clears the old device values immediately', async ({ page }) => {
  await ready(page);
  await page.locator('#openAddBtn').click();
  await page.locator('#deviceIdInput').fill('unknown-ui-device');
  await page.route('**/api/events', route => route.abort());
  await page.route('**/api/status', async route => {
    const response = await route.fetch(); const json = await response.json();
    json.devices = json.devices.filter(d => d.deviceId !== 'unknown-ui-device');
    json.commands = json.commands.filter(d => d.deviceId !== 'unknown-ui-device');
    await route.fulfill({ response, json });
  });
  await page.locator('#addDeviceBtn').click();
  await expect(page.locator('#selectedDeviceName')).toHaveText('unknown-ui-device');
  await expect(page.locator('#currentTemp')).toHaveText('—');
  await expect(page.locator('#currentSetTemp')).toHaveText('—');
  await expect(page.locator('#tempUpBtn')).toBeDisabled();
});
test('Mobile layout has no horizontal overflow and dialogs fit', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await ready(page);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await page.screenshot({ path: 'artifacts/ui-mobile.png', fullPage: true });
  await page.locator('.power-details summary').click();
  await page.locator('#stopBtn').click();
  const box = await page.locator('#confirmDialog').boundingBox();
  expect(box.x).toBeGreaterThanOrEqual(0);
  expect(box.width).toBeLessThanOrEqual(390);
  await page.screenshot({ path: 'artifacts/ui-confirm.png' });
});
test('Security details accurately explain cloud authorization boundary', async ({ page }) => {
  await ready(page);
  await page.locator('#openSecurityBtn').click();
  await expect(page.locator('#securityDialog')).toContainText('无法阻止第三方绕过面板');
});

test('Fractional temperatures still produce an exact one degree target', async ({ page }) => {
  let changed = false;
  await page.route('**/api/events', route => route.abort());
  await page.route('**/api/status', async route => {
    const response = await route.fetch(); const json = await response.json();
    for (const device of json.devices) device.payload.set_temp = changed ? 24.6 : 23.6;
    await route.fulfill({ response, json });
  });
  const sent = writes(page);
  await page.route('**/api/control', async route => {
    const body = route.request().postDataJSON();
    if (body.action === 'get_data') return route.continue();
    changed = true;
    await route.fulfill({ status: 202, json: { success: true, state: 'pending', message: 'Waiting for report', commandId: body.commandId } });
  });
  await page.goto('/');
  await expect(page.locator('#currentSetTemp')).toHaveText('23.6');
  await page.locator('#tempUpBtn').click();
  await page.locator('#applyTempBtn').click();
  await expect(page.locator('#confirmDialog')).not.toBeVisible();
  await expect(page.locator('#currentSetTemp')).toHaveText('24.6');
  expect(sent[0]).toMatchObject({ value: 24.6, expectedValue: 23.6 });
});
test('Water temperature has its own freshness label', async ({ page }) => {
  await page.route('**/api/events', route => route.abort());
  await page.route('**/api/status', async route => {
    const response = await route.fetch(); const json = await response.json();
    for (const device of json.devices) device.fieldSeenAt.sj_temp = json.serverTime - 60_000;
    await route.fulfill({ response, json });
  });
  await ready(page);
  await expect(page.locator('#waterReadingLabel')).toHaveText('水温回读已过期');
  await expect(page.locator('#currentTemp')).toHaveAttribute('data-stale', 'true');
});
test('Pump slider is constrained to integer gears one through ten', async ({ page }) => {
  await page.route('**/api/events', route => route.abort());
  await page.route('**/api/status', async route => {
    const response = await route.fetch(); const json = await response.json();
    for (const device of json.devices) device.payload.wind_speed_set = 1;
    await route.fulfill({ response, json });
  });
  await ready(page);
  await expect(page.locator('#pumpSlider')).toBeEnabled();
  await expect(page.locator('#pumpSlider')).toHaveAttribute('min', '1');
  await expect(page.locator('#pumpSlider')).toHaveAttribute('max', '10');
  await expect(page.locator('#pumpSlider')).toHaveAttribute('step', '1');
  await expect(page.locator('#pumpTarget')).toHaveText('1');
  await expect(page.locator('#applyPumpBtn')).toBeDisabled();
});

test('Invalid or unchanged temperature never opens confirmation or publishes', async ({ page }) => {
  await ready(page);
  const sent = writes(page);
  for (const value of ['51', '-21', '', '23']) {
    await page.locator('#temperatureInput').fill(value);
    await expect(page.locator('#applyTempBtn')).toBeDisabled();
  }
  expect(sent).toHaveLength(0);
});
test('Polling preserves edited targets until device selection changes', async ({ page }) => {
  await ready(page);
  await page.locator('#temperatureInput').fill('11.25');
  await page.locator('#pumpSlider').fill('7');
  await page.locator('#refreshBtn').click();
  await expect(page.locator('#temperatureInput')).toHaveValue('11.25');
  await expect(page.locator('#pumpSlider')).toHaveValue('7');
  await page.locator('#openAddBtn').click();
  await page.locator('#deviceIdInput').fill('draft-reset-device');
  await page.locator('#addDeviceBtn').click();
  await expect(page.locator('#temperatureInput')).toHaveValue('23');
  await expect(page.locator('#pumpSlider')).toHaveValue('5');
});
test('Opening the page immediately queries the selected device', async ({ page }) => {
  const queries = [];
  page.on('request', request => {
    if (request.url().endsWith('/api/control') && request.method() === 'POST' && request.postDataJSON().action === 'get_data') queries.push(request.postDataJSON());
  });
  const started = Date.now();
  await ready(page);
  expect(queries.some(q => q.deviceId === deviceId)).toBe(true);
  expect(Date.now() - started).toBeLessThan(1000);
});
test('Update age never goes backwards for the same water report', async ({ page, request }) => {
  const snapshot = await (await request.get('/api/status')).json();
  const baselineTime = snapshot.serverTime;
  snapshot.devices[0].fieldSeenAt.sj_temp = baselineTime - 9000;
  let late = false;
  await page.route('**/api/events', route => route.abort());
  await page.route('**/api/control', route => route.fulfill({ json: { success: true, state: 'requested' } }));
  await page.route('**/api/status', route => route.fulfill({ json: { ...snapshot, serverTime: baselineTime - (late ? 5000 : 0) } }));
  await ready(page);
  await expect(page.locator('#lastSeenLabel')).toHaveText('9 秒前更新');
  late = true;
  await page.locator('#refreshBtn').click();
  const label = await page.locator('#lastSeenLabel').textContent();
  expect(parseInt(label)).toBeGreaterThanOrEqual(9);
});
test('New real-time water reports reset the age immediately', async ({ page }) => {
  await ready(page);
  const baseline = await page.locator('#statusOutput').textContent();
  const stamp = JSON.parse(baseline).fieldSeenAt.sj_temp;
  await expect.poll(async () => JSON.parse(await page.locator('#statusOutput').textContent()).fieldSeenAt.sj_temp, { timeout: 2000 }).toBeGreaterThan(stamp);
  await expect(page.locator('#lastSeenLabel')).toHaveText('刚刚更新');
});
test('Dark theme and primary color persist on disk and across browser origins without cache', async ({ page, request }) => {
  await ready(page);
  await expect(page.locator('.sidebar, .nav-item')).toHaveCount(0);
  await page.locator('#appearanceBtn').click();
  await page.locator('#themeSelect').selectOption('dark');
  await page.locator('#accentHex').fill('#8a5ccf');
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
  expect(await page.evaluate(() => getComputedStyle(document.documentElement).getPropertyValue('--accent').trim())).toBe('#8a5ccf');
  await page.locator('#saveAppearanceBtn').click();
  await expect(page.locator('#appearanceDialog')).not.toBeVisible();
  expect(JSON.parse(await readFile('.test-data/ui/appearance.json', 'utf8'))).toEqual({ theme: 'dark', accent: '#8a5ccf' });
  expect(await (await request.get('/api/preferences/appearance')).json()).toMatchObject({ theme: 'dark', accent: '#8a5ccf', saved: true });
  await page.screenshot({ path: 'artifacts/ui-dark.png', fullPage: true });
  await page.evaluate(() => localStorage.clear());
  await page.reload();
  await expect(page.locator('#appearanceBtn')).toBeEnabled();
  await page.goto('http://localhost:43121/');
  await expect(page.locator('#appearanceBtn')).toBeEnabled();
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
  await page.locator('#appearanceBtn').click();
  await expect(page.locator('#accentHex')).toHaveValue('#8a5ccf');
  await expect(page.locator('#themeSelect')).toHaveValue('dark');
});
test('Appearance save failures stay visible and finishing retries the disk save', async ({ page, request }) => {
  await ready(page);
  await page.route('**/api/preferences/appearance', route => route.request().method() === 'PUT'
    ? route.fulfill({ status: 503, json: { error: 'Offline test write failure' } }) : route.continue());
  await page.locator('#appearanceBtn').click();
  await page.locator('#themeSelect').selectOption('dark');
  await page.locator('#saveAppearanceBtn').click();
  await expect(page.locator('#appearanceDialog')).toBeVisible();
  await expect(page.locator('#appearanceSaveStatus')).toContainText('保存失败');
  await page.unroute('**/api/preferences/appearance');
  await page.locator('#saveAppearanceBtn').click();
  await expect(page.locator('#appearanceDialog')).not.toBeVisible();
  expect(await (await request.get('/api/preferences/appearance')).json()).toMatchObject({ theme: 'dark', saved: true });
});
test('System theme responds to changes while an explicit theme stays fixed', async ({ page }) => {
  await page.emulateMedia({ colorScheme: 'light' });
  await ready(page);
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');
  await page.emulateMedia({ colorScheme: 'dark' });
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
  await page.locator('#appearanceBtn').click();
  await page.locator('#themeSelect').selectOption('light');
  await page.emulateMedia({ colorScheme: 'light' });
  await page.emulateMedia({ colorScheme: 'dark' });
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');
});
test('White and black custom colors maintain readable button and chart colors', async ({ page }) => {
  await ready(page);
  await page.locator('#appearanceBtn').click();
  for (const theme of ['light', 'dark']) {
    await page.locator('#themeSelect').selectOption(theme);
    for (const color of ['#ffffff', '#000000']) {
      await page.locator('#accentHex').fill(color);
      const colors = await page.evaluate(() => {
        const style = getComputedStyle(document.documentElement);
        return ['--accent', '--on-accent', '--accent-ink', '--surface'].map(key => style.getPropertyValue(key).trim());
      });
      const luminance = value => {
        const hex = value.length === 4 ? '#' + [...value.slice(1)].map(c => c + c).join('') : value;
        return [1, 3, 5].map(i => parseInt(hex.slice(i, i + 2), 16) / 255)
        .map(v => v <= .04045 ? v / 12.92 : ((v + .055) / 1.055) ** 2.4)
        .reduce((sum, v, i) => sum + v * [.2126, .7152, .0722][i], 0);
      };
      const contrast = (a, b) => (Math.max(luminance(a), luminance(b)) + .05) / (Math.min(luminance(a), luminance(b)) + .05);
      expect(contrast(colors[0], colors[1])).toBeGreaterThanOrEqual(4.5);
      expect(contrast(colors[2], colors[3])).toBeGreaterThanOrEqual(4.5);
    }
  }
  await page.locator('#accentHex').fill('#zzzzzz');
  expect(await page.evaluate(() => getComputedStyle(document.documentElement).getPropertyValue('--accent').trim())).toBe('#000000');
  await page.locator('#resetAppearanceBtn').click();
  await expect(page.locator('#accentHex')).toHaveValue('#147c6e');
});
