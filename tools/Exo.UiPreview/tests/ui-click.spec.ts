import { expect, test } from '@playwright/test'

test('Exo UI preview click navigation', async ({ page }) => {
  await page.goto('/')

  await expect(page.getByTestId('exo-shell')).toBeVisible()
  await expect(page.getByTestId('page-home')).toBeVisible()
  await expect(page.getByTestId('hero-brand')).toHaveText('Exo')
  await expect(page.getByTestId('hero-tagline')).toBeVisible()
  await expect(page.getByTestId('home-frame')).toBeVisible()
  await expect(page.getByTestId('home-fps')).toBeVisible()
  await expect(page.getByTestId('home-frametime')).toBeVisible()
  await expect(page.getByTestId('home-stats')).toBeVisible()
  await expect(page.getByTestId('home-stat-path')).toBeVisible()
  await expect(page.getByTestId('card-windows')).toBeVisible()

  await page.getByTestId('nav-discord').click()
  await expect(page.getByTestId('page-discord')).toBeVisible()
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
  await page.getByTestId('btn-display-panel').click()
  await expect(page.getByTestId('page-nvidia-panel')).toBeVisible()
  await expect(page.getByTestId('btn-open-cpl')).toBeVisible()
  await expect(page.getByTestId('display-display-1')).toBeVisible()

  await page.getByTestId('btn-back').click()
  await expect(page.getByTestId('page-nvidia')).toBeVisible()

  await page.getByTestId('nav-home').click()
  await expect(page.getByTestId('page-home')).toBeVisible()
  await expect(page.getByTestId('home-fps')).toBeVisible()

  await page.getByTestId('nav-settings').click()
  await expect(page.getByTestId('settings-flyout')).toBeVisible()
  await page.getByTestId('settings-backdrop').click()
  await expect(page.getByTestId('settings-flyout')).toHaveCount(0)
})
