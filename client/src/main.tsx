import { StrictMode, Suspense, lazy } from 'react'
import { createRoot } from 'react-dom/client'
import './index.css'
import App from './App.tsx'
import { ThemeModeProvider } from './theme/ThemeModeProvider'

// The call map is its own page (opened in a new tab from the dashboard). It is loaded only
// on /map, so Leaflet stays out of the dashboard's bundle. The server serves index.html for
// any unknown path, so /map needs no server route.
const MapPage = lazy(() => import('./pages/MapPage'))
const isMapPage = window.location.pathname.replace(/\/+$/, '') === '/map'

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <ThemeModeProvider>
      {isMapPage ? (
        <Suspense fallback={null}>
          <MapPage />
        </Suspense>
      ) : (
        <App />
      )}
    </ThemeModeProvider>
  </StrictMode>,
)
