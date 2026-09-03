import com.sun.net.httpserver.HttpExchange;
import com.sun.net.httpserver.HttpHandler;
import com.sun.net.httpserver.HttpServer;

import java.io.IOException;
import java.io.InputStream;
import java.net.InetSocketAddress;
import java.net.URI;
import java.net.http.HttpClient;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.time.Duration;
import java.util.List;
import java.util.concurrent.atomic.AtomicBoolean;

/**
 * Loest das Render-Deploy-Problem aus CONCEPT.md Abschnitt 6.26: GraphHopper (ein einzelner
 * Java-Prozess) oeffnet seinen HTTP-Port erst NACH dem kompletten Import (OSM-Parsing +
 * Urban-Density-Berechnung, mehrere Minuten) - Render's Health-Check-Fenster beim Deploy ist
 * aber fest auf 15 Minuten begrenzt und laut eigener Doku nicht konfigurierbar
 * (render.com/docs/health-checks). Dieser Proxy laeuft auf dem extern exponierten Port (8989,
 * unveraendert gegenueber render.yaml/EXPOSE) und antwortet auf /health SOFORT mit 200 - lange
 * bevor GraphHopper selbst (intern auf einem anderen Port, siehe graphhopper-config.yml)
 * ueberhaupt fertig importiert hat. Alle anderen Anfragen werden erst weitergeleitet, sobald
 * GraphHoppers EIGENER /health-Endpunkt (auf dem internen Port) zum ersten Mal erfolgreich
 * antwortet - bis dahin liefert dieser Proxy 503, statt Anfragen haengen zu lassen oder ins
 * Leere zu laufen.
 *
 * Bewusst nur JDK-Bordmittel (com.sun.net.httpserver, java.net.http) - keine neue
 * Docker-Abhaengigkeit (z.B. nginx/socat), da Java im Image ohnehin vorhanden ist.
 */
public final class HealthProxy {
    private static final Duration POLL_INTERVAL = Duration.ofSeconds(2);
    private static final Duration REQUEST_TIMEOUT = Duration.ofSeconds(60);
    // Wenn GraphHopper nach dieser Zeit immer noch nicht bereit ist, stimmt etwas grundlegend
    // nicht (Absturz, haengengebliebener Import) - der Proxy beendet sich dann selbst, statt
    // fuer immer 503 zu liefern, damit Render/Docker den Container neu startet und das Problem
    // sichtbar wird (statt sich hinter einem dauerhaft "gesunden" Health-Check zu verstecken).
    private static final Duration MAX_WAIT_FOR_READY = Duration.ofMinutes(30);

    private static final AtomicBoolean ready = new AtomicBoolean(false);

    public static void main(String[] args) throws IOException {
        int externalPort = Integer.parseInt(envOrDefault("PROXY_PORT", "8989"));
        int internalPort = Integer.parseInt(envOrDefault("GRAPHHOPPER_INTERNAL_PORT", "18989"));
        String internalBase = "http://127.0.0.1:" + internalPort;

        HttpClient client = HttpClient.newBuilder()
                .connectTimeout(Duration.ofSeconds(5))
                .build();

        HttpServer server = HttpServer.create(new InetSocketAddress(externalPort), 0);
        server.createContext("/health", exchange -> respondHealthy(exchange));
        server.createContext("/", exchange -> proxyOrReject(exchange, client, internalBase));
        server.setExecutor(null);
        server.start();
        System.out.println("[HealthProxy] listening on :" + externalPort + ", forwarding to " + internalBase
                + " once GraphHopper is ready");

        pollUntilReady(client, internalBase + "/health");
    }

    private static void respondHealthy(HttpExchange exchange) throws IOException {
        byte[] body = "{}".getBytes();
        exchange.getResponseHeaders().add("Content-Type", "application/json");
        exchange.sendResponseHeaders(200, body.length);
        exchange.getResponseBody().write(body);
        exchange.close();
    }

    private static void proxyOrReject(HttpExchange exchange, HttpClient client, String internalBase) throws IOException {
        if (!ready.get()) {
            byte[] body = ("{\"message\":\"GraphHopper importiert die Kartendaten noch, "
                    + "bitte in Kuerze erneut versuchen.\"}").getBytes();
            exchange.getResponseHeaders().add("Content-Type", "application/json");
            exchange.sendResponseHeaders(503, body.length);
            exchange.getResponseBody().write(body);
            exchange.close();
            return;
        }

        try {
            byte[] requestBody = exchange.getRequestBody().readAllBytes();
            URI target = URI.create(internalBase + exchange.getRequestURI());

            HttpRequest.Builder builder = HttpRequest.newBuilder(target).timeout(REQUEST_TIMEOUT);
            exchange.getRequestHeaders().forEach((name, values) -> {
                // "Host" darf nicht 1:1 durchgereicht werden (zeigt sonst auf den externen statt
                // internen Host) - java.net.http setzt es ohnehin selbst neu.
                if (!name.equalsIgnoreCase("Host") && !name.equalsIgnoreCase("Content-Length")) {
                    for (String value : values) {
                        try {
                            builder.header(name, value);
                        } catch (IllegalArgumentException ignored) {
                            // restricted header (z.B. von der JVM selbst verwaltet) - ueberspringen
                        }
                    }
                }
            });
            builder.method(exchange.getRequestMethod(), HttpRequest.BodyPublishers.ofByteArray(requestBody));

            HttpResponse<byte[]> response = client.send(builder.build(), HttpResponse.BodyHandlers.ofByteArray());

            response.headers().map().forEach((name, values) -> {
                if (!name.equalsIgnoreCase("Transfer-Encoding") && !name.equalsIgnoreCase("Content-Length")) {
                    for (String value : values) {
                        exchange.getResponseHeaders().add(name, value);
                    }
                }
            });
            exchange.sendResponseHeaders(response.statusCode(), response.body().length);
            exchange.getResponseBody().write(response.body());
        } catch (Exception e) {
            System.err.println("[HealthProxy] proxy error: " + e);
            byte[] body = "{\"message\":\"GraphHopper nicht erreichbar.\"}".getBytes();
            exchange.getResponseHeaders().add("Content-Type", "application/json");
            exchange.sendResponseHeaders(502, body.length);
            exchange.getResponseBody().write(body);
        } finally {
            exchange.close();
        }
    }

    private static void pollUntilReady(HttpClient client, String healthUrl) {
        long start = System.nanoTime();
        HttpRequest healthCheck = HttpRequest.newBuilder(URI.create(healthUrl))
                .timeout(Duration.ofSeconds(3))
                .GET()
                .build();

        while (true) {
            try {
                HttpResponse<Void> response = client.send(healthCheck, HttpResponse.BodyHandlers.discarding());
                if (response.statusCode() >= 200 && response.statusCode() < 300) {
                    ready.set(true);
                    long elapsedSeconds = Duration.ofNanos(System.nanoTime() - start).toSeconds();
                    System.out.println("[HealthProxy] GraphHopper ready after " + elapsedSeconds
                            + "s - now proxying requests");
                    return;
                }
            } catch (Exception e) {
                // Erwarteter Fall waehrend des Imports (Port noch nicht offen) - kein Fehler-Log,
                // sonst flutet das die Render-Logs waehrend der gesamten Importzeit.
            }

            if (Duration.ofNanos(System.nanoTime() - start).compareTo(MAX_WAIT_FOR_READY) > 0) {
                System.err.println("[HealthProxy] GraphHopper wurde nach " + MAX_WAIT_FOR_READY.toMinutes()
                        + " Minuten immer noch nicht bereit - beende mich, damit der Container neu startet.");
                System.exit(1);
            }

            try {
                Thread.sleep(POLL_INTERVAL.toMillis());
            } catch (InterruptedException ignored) {
                Thread.currentThread().interrupt();
                return;
            }
        }
    }

    private static String envOrDefault(String name, String fallback) {
        String value = System.getenv(name);
        return value == null || value.isBlank() ? fallback : value;
    }
}
