// Kopiert die von maplibre-gl selbst vorgefertigten Dist-Dateien unveraendert nach
// public/vendor/maplibre-gl, damit sie von Vite/Rolldown ueberhaupt nicht transformiert
// werden (siehe Kommentar in vite.config.ts fuer den Grund). Laeuft automatisch nach
// jedem "npm install" (postinstall), damit die Kopie immer zur in package-lock.json
// gepinnten Version passt.
import { cpSync, mkdirSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

const __dirname = dirname(fileURLToPath(import.meta.url));
const srcDir = join(__dirname, "..", "node_modules", "maplibre-gl", "dist");
const destDir = join(__dirname, "..", "public", "vendor", "maplibre-gl");

// maplibre-gl.mjs und maplibre-gl-worker.mjs importieren beide relativ von
// ./maplibre-gl-shared.mjs - muss also mitkopiert werden, sonst 404 beim Laden.
const files = ["maplibre-gl.mjs", "maplibre-gl-worker.mjs", "maplibre-gl-shared.mjs", "maplibre-gl.css"];

mkdirSync(destDir, { recursive: true });
for (const file of files) {
  cpSync(join(srcDir, file), join(destDir, file));
}
