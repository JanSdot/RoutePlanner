import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// https://vite.dev/config/
//
// maplibre-gl bundles its own Web Worker (maplibre-gl.mjs internally does
// `new Worker(new URL("./maplibre-gl-worker.mjs", import.meta.url))`). If Rolldown (Vite 8's
// bundler) transforms/re-emits that code itself for the production build - whether split into
// a separate chunk, inlined as a Blob, minified or not - the resulting main-thread/worker pair
// silently stops communicating: GeoJSON sources never produce tiles, no error is thrown
// anywhere. Verified by testing every combination (worker.format, minify on/off, chunk-split vs
// inlined) - all broken the same way. The only combination that actually works is serving
// maplibre-gl's own prebuilt dist files completely untouched, so its internal relative Worker
// URL resolves to an equally untouched sibling file.
//
// `scripts/copy-maplibre-assets.mjs` (run via postinstall) copies those files into
// public/vendor/maplibre-gl. `build.rollupOptions.external` tells Rolldown to leave every
// `import ... from "maplibre-gl"` in MapView.tsx completely untouched in the output bundle
// instead of resolving/transforming it; the import map in index.html then tells the BROWSER
// to resolve that bare specifier to the untouched vendor copy at runtime. optimizeDeps.exclude
// covers the dev server the same way (serves node_modules directly, unbundled) - the import
// map is simply unused there since Vite's dev server always rewrites bare specifiers to
// explicit URLs itself before the browser sees them.
export default defineConfig({
  plugins: [react()],
  optimizeDeps: {
    exclude: ["maplibre-gl"],
  },
  build: {
    rollupOptions: {
      external: ["maplibre-gl"],
    },
  },
})
