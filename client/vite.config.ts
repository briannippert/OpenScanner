/// <reference types="vitest" />
import { defineConfig } from 'vitest/config'
import react from '@vitejs/plugin-react'
import { VitePWA } from 'vite-plugin-pwa'

// https://vite.dev/config/
export default defineConfig({
  plugins: [
    react(),
    VitePWA({
      // No offline cache. OpenScanner is a live view of a device on the LAN: it is
      // useless without the server, so precaching buys nothing, while a service
      // worker sitting between the browser and a self-signed, self-updating server
      // causes real trouble (stale UI after an update, intercepted requests failing
      // when the cache or the certificate exception is unavailable).
      //
      // selfDestroying ships a tiny sw.js whose only job is to unregister the
      // caching worker that earlier builds installed and delete its caches, so
      // browsers that already have one are cleaned up on their next visit. The web
      // app manifest is still generated, so the app stays installable.
      selfDestroying: true,
      manifest: {
        name: 'OpenScanner',
        short_name: 'OpenScanner',
        description: 'P25 Digital Radio Scanner Dashboard',
        theme_color: '#08090b',
        background_color: '#08090b',
        display: 'standalone',
        icons: [
          {
            src: 'favicon.svg',
            sizes: '192x192',
            type: 'image/svg+xml'
          },
          {
            src: 'favicon.svg',
            sizes: '512x512',
            type: 'image/svg+xml'
          },
          {
            src: 'favicon.svg',
            sizes: '512x512',
            type: 'image/svg+xml',
            purpose: 'any maskable'
          }
        ]
      }
    })
  ],
  server: {
    allowedHosts: true,
    proxy: {
      '/api': {
        target: 'http://localhost:5212',
        changeOrigin: true,
        secure: false,
      },
      '/audio': {
        target: 'http://localhost:5212',
        changeOrigin: true,
        secure: false,
      },
      '/swagger': {
        target: 'http://localhost:5212',
        changeOrigin: true,
        secure: false,
      },
      '/ws': {
        target: 'ws://localhost:5212',
        ws: true,
        changeOrigin: true,
      }
    }
  },
  test: {
    globals: true,
    environment: 'jsdom',
    setupFiles: './src/setupTests.ts',
    exclude: ['**/e2e/**', '**/node_modules/**', '**/dist/**']
  }
})
