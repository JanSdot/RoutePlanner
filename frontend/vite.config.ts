import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  // maplibre-gl bundles its own Web Worker; letting Vite's dependency pre-bundling
  // process it separately from the main-thread chunk can cause a protocol mismatch where
  // GeoJSON sources never produce tiles (silently - no error, no data ever renders).
  // Excluding it from optimizeDeps avoids that split.
  optimizeDeps: {
    exclude: ["maplibre-gl"],
  },
})
