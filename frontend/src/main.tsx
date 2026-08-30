import { createRoot } from 'react-dom/client'
import './index.css'
import App from './App.tsx'

// Kein StrictMode: MapLibre haelt ein teures, zustandsbehaftetes WebGL-Kartenobjekt, das mit
// StrictMode-Dev-Verhalten (Effekte werden zu Testzwecken doppelt gemountet) nicht sauber
// zusammenspielt - das Kartenobjekt der zweiten Mount-Runde hat nie zuverlaessig sein "load"-
// Event gefeuert (verifiziert), wodurch Route-Overlays nie gezeichnet wurden.
createRoot(document.getElementById('root')!).render(<App />)
