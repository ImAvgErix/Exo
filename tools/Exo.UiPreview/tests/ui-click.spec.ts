import { expect, test } from '@playwright/test'

test('Exo UI preview click navigation', async ({ page }) => {
  await page.goto('/')

  await expect(page.getByTestId('exo-shell')).toBeVisible()
  await expect(page.getByTestId('page-home')).toBeVisible()
  await expect(page.getByTestId('hero-brand')).toHaveText('EXO')
  await expect(page.getByTestId('hero-tagline')).toBeVisible()
  // On home the top-bar EXO control is hidden (page brand owns it).
  await expect(page.getByTestId('nav-home')).toHaveCount(0)
  await expect(page.getByTestId('home-memory')).toBeVisible()
  await expect(page.getByTestId('home-stat-discord')).toBeVisible()
  await expect(page.getByTestId('home-stat-steam')).toBeVisible()
  await expect(page.getByTestId('home-stat-connection')).toBeVisible()
  await expect(page.getByTestId('home-stat-gpu')).toBeVisible()

  await page.getByTestId('nav-discord').click()
  await expect(page.getByTestId('page-discord')).toBeVisible()
  await expect(page.getByTestId('nav-home')).toBeVisible()
  await expect(page.getByTestId('btn-apply')).toBeVisible()
  await page.getByTestId('btn-apply').click()
  await expect(page.getByTestId('action-message')).toBeVisible()

  await page.getByTestId('nav-steam').click()
  await expect(page.getByTestId('page-steam')).toBeVisible()
  await expect(page.getByTestId('btn-repair')).toBeVisible()
  await expect(page.getByTestId('btn-refresh')).toBeVisible()

  await page.getByTestId('nav-internet').click()
  await expect(page.getByTestId('page-internet')).toBeVisible()
  await expect(page.getByTestId('btn-low-latency')).toBeVisible()
  await expect(page.getByTestId('btn-highest-download')).toBeVisible()

  await page.getByTestId('nav-nvidia').click()
  await expect(page.getByTestId('page-nvidia')).toBeVisible()
  await expect(page.getByTestId('toggle-gsync')).toBeVisible()
  await page.getByTestId('btn-open-nvidia-cpl').click()
  await expect(page.getByTestId('action-message')).toContainText('Control Panel')

  await page.getByTestId('nav-home').click()
  await expect(page.getByTestId('page-home')).toBeVisible()
  await expect(page.getByTestId('nav-home')).toHaveCount(0)
  await expect(page.getByTestId('home-memory')).toBeVisible()

  await page.getByTestId('nav-settings').click()
  await expect(page.getByTestId('settings-flyout')).toBeVisible()
  await page.getByTestId('settings-backdrop').click()
  await expect(page.getByTestId('settings-flyout')).toHaveCount(0)
})
