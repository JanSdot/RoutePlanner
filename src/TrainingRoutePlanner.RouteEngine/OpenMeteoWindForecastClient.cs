using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using TrainingRoutePlanner.Domain;

namespace TrainingRoutePlanner.RouteEngine;

/// <summary>Windvorhersage fuer einen Ort/Zeitpunkt - siehe CONCEPT.md Phase-4-Backlog
/// "Windmodellierung". Liefert null statt zu werfen, wenn keine Vorhersage verfuegbar ist (z.B.
/// API-Fehler, Zeitpunkt zu weit in der Vergangenheit/Zukunft) - Wind ist ein rein additives
/// Zeitschaetzungs-Feature, ein Ausfall darf die Routenberechnung selbst nie verhindern.</summary>
public interface IWindForecastClient
{
    Task<WindConditions?> GetForecastAsync(GeoPoint location, DateTimeOffset atTime, CancellationToken ct = default);
}

/// <summary>Thin wrapper um Open-Meteos kostenlose, schluessellose Forecast-API (siehe
/// CONCEPT.md Phase-4-Backlog "Windmodellierung" - bewusst gegen kostenpflichtige
/// Wetter-/Verkehrs-APIs entschieden, wie schon bei den verworfenen Traffic-Data-Optionen in
/// Abschnitt 7).</summary>
public sealed class OpenMeteoWindForecastClient(HttpClient http) : IWindForecastClient
{
    public async Task<WindConditions?> GetForecastAsync(GeoPoint location, DateTimeOffset atTime, CancellationToken ct = default)
    {
        var dateUtc = atTime.ToUniversalTime();
        var dateString = dateUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        // wind_speed_unit=ms, damit direkt in den von PowerSpeedModel erwarteten m/s geliefert
        // wird, keine Umrechnung noetig. start_date/end_date auf denselben Tag begrenzt statt
        // die volle mehrtaegige Standard-Vorhersage abzurufen - wir brauchen nur eine Stunde
        // daraus.
        var url = "/v1/forecast" +
            $"?latitude={location.Lat.ToString(CultureInfo.InvariantCulture)}" +
            $"&longitude={location.Lon.ToString(CultureInfo.InvariantCulture)}" +
            "&hourly=wind_speed_10m,wind_direction_10m&wind_speed_unit=ms&timezone=UTC" +
            $"&start_date={dateString}&end_date={dateString}";

        OpenMeteoResponse? response;
        try
        {
            response = await http.GetFromJsonAsync<OpenMeteoResponse>(url, ct);
        }
        catch (HttpRequestException)
        {
            return null;
        }

        var hourly = response?.Hourly;
        if (hourly?.Time is null || hourly.WindSpeed10m is null || hourly.WindDirection10m is null)
            return null;

        // Open-Meteo liefert stuendliche Zeitstempel im Format "yyyy-MM-ddTHH:mm" (ohne
        // Sekunden) - auf die volle Stunde des angefragten Zeitpunkts gerundet, um exakt einen
        // dieser Zeitstempel zu treffen.
        var targetHour = new DateTime(dateUtc.Year, dateUtc.Month, dateUtc.Day, dateUtc.Hour, 0, 0, DateTimeKind.Utc);
        var targetHourString = targetHour.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);
        var index = Array.IndexOf(hourly.Time, targetHourString);
        if (index < 0 || index >= hourly.WindSpeed10m.Length || index >= hourly.WindDirection10m.Length)
            return null;

        return new WindConditions(hourly.WindSpeed10m[index], hourly.WindDirection10m[index]);
    }

    private sealed class OpenMeteoResponse
    {
        [JsonPropertyName("hourly")]
        public OpenMeteoHourly? Hourly { get; set; }
    }

    private sealed class OpenMeteoHourly
    {
        [JsonPropertyName("time")]
        public string[]? Time { get; set; }

        [JsonPropertyName("wind_speed_10m")]
        public double[]? WindSpeed10m { get; set; }

        [JsonPropertyName("wind_direction_10m")]
        public double[]? WindDirection10m { get; set; }
    }
}
