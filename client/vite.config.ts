/// <reference types="vitest" />
import { defineConfig } from 'vitest/config'
import react from '@vitejs/plugin-react'
import { VitePWA } from 'vite-plugin-pwa'

// https://vite.dev/config/
export default defineConfig({
  plugins: [
    react(),
    VitePWA({
      registerType: 'autoUpdate',
      includeAssets: ['favicon.ico', 'apple-touch-icon.png', 'mask-icon.svg'],
      workbox: {
        globPatterns: ['**/*.{js,css,html,ico,png,svg}'],
        navigateFallbackDenylist: [/^\/api/, /^\/swagger/, /^\/audio/, /^\/ws/]
      },
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
