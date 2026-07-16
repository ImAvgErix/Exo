import { chromium } from 'playwright'
import { spawn } from 'node:child_process'
import { setTimeout as sleep } from 'node:timers/promises'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import fs from 'node:fs'

const root = path.dirname(path.dirname(fileURLToPath(import.meta.url)))
const out = process.argv[2] || '/opt/cursor/artifacts/ui-preview'
fs.mkdirSync(out, { recursive: true })

const vite = spawn('npm', ['run', 'dev', '--', '--host', '127.0.0.1', '--port', '5173'], {
  cwd: root,
  stdio: ['ignore', 'pipe', 'pipe'],
})

async function waitReady() {
  for (let i = 0; i < 40; i++) {
    try {
      const r = await fetch('http://127.0.0.1:5173/')
      if (r.ok) return
    } catch {}
    await sleep(250)
  }
  throw new Error('vite not ready')
}

async function settle(page, testId) {
  await page.getByTestId(testId).waitFor({ state: 'visible' })
  await sleep(450)
}

try {
  await waitReady()
  const browser = await chromium.launch()
  const page = await browser.newPage({ viewport: { width: 1280, height: 840 } })
  await page.goto('http://127.0.0.1:5173/', { waitUntil: 'networkidle' })
  await settle(page, 'page-home')
  await page.screenshot({ path: `${out}/01-home.png` })

  await page.getByTestId('nav-discord').click()
  await settle(page, 'page-discord')
  await page.getByTestId('discord-features').waitFor({ state: 'visible' })
  await sleep(200)
  await page.screenshot({ path: `${out}/02-discord.png` })

  await page.getByTestId('nav-internet').click()
  await settle(page, 'page-internet')
  await page.screenshot({ path: `${out}/03-internet.png` })

  await page.getByTestId('nav-nvidia').click()
  await settle(page, 'page-nvidia')
  await page.screenshot({ path: `${out}/04-nvidia.png` })

  await page.getByTestId('btn-display-panel').click()
  await settle(page, 'page-nvidia-panel')
  await page.screenshot({ path: `${out}/05-nvidia-panel.png` })

  await page.getByTestId('nav-settings').click()
  await settle(page, 'settings-flyout')
  await page.screenshot({ path: `${out}/06-settings.png` })

  await browser.close()
  console.log('screenshots written to', out)
} finally {
  vite.kill('SIGTERM')
}
