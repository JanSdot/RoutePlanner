using System.Globalization;
using System.Linq;
using System.Text;
using System.Xml;
using TrainingRoutePlanner.Domain;

namespace TrainingRoutePlanner.RouteEngine;

/// <summary>Exportiert ein RouteResult als GPX 1.1, siehe CONCEPT.md Abschnitt 6 (Phase 2:
/// "GPX-Export für Radcomputer/Garmin").</summary>
public static class GpxWriter
{
    public static string ToGpx(RouteResult result, string routeName = "Trainingsroute")
    {
        var settings = new XmlWriterSettings
        {
            Indent = true,
            Encoding = Encoding.UTF8,
        };

        using var stringWriter = new Utf8StringWriter();
        using (var writer = XmlWriter.Create(stringWriter, settings))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("gpx", "http://www.topografix.com/GPX/1/1");
            writer.WriteAttributeString("version", "1.1");
            writer.WriteAttributeString("creator", "TrainingRoutePlanner");

            // Benannte Wegpunkte an Start/Ende jedes Intervall-Segments - Garmin- und
            // Wahoo-Geraete zeigen beim Abfahren einer Kurs-GPX eine Pop-up-Meldung, sobald
            // ein benannter Wegpunkt in der Naehe erreicht wird. Muss vor <trk> stehen
            // (GPX-1.1-Schema-Reihenfolge: wpt* vor rte* vor trk*).
            var labelOccurrence = new Dictionary<string, int>();
            foreach (var segment in result.Segments)
            {
                var occurrence = labelOccurrence.TryGetValue(segment.Label, out var count) ? count + 1 : 1;
                labelOccurrence[segment.Label] = occurrence;
                var suffix = result.Segments.Count(s => s.Label == segment.Label) > 1 ? $" ({occurrence})" : "";

                WriteWaypoint(writer, segment.Geometry[0], $"Start: {segment.Label}{suffix}");
                WriteWaypoint(writer, segment.Geometry[^1], $"Ende: {segment.Label}{suffix}");
            }

            writer.WriteStartElement("trk");
            writer.WriteElementString("name", routeName);
            writer.WriteStartElement("trkseg");

            foreach (var point in result.Geometry)
            {
                writer.WriteStartElement("trkpt");
                writer.WriteAttributeString("lat", point.Lat.ToString("F6", CultureInfo.InvariantCulture));
                writer.WriteAttributeString("lon", point.Lon.ToString("F6", CultureInfo.InvariantCulture));
                if (point.Elevation.HasValue)
                    writer.WriteElementString("ele", point.Elevation.Value.ToString("F1", CultureInfo.InvariantCulture));
                writer.WriteEndElement();
            }

            writer.WriteEndElement(); // trkseg
            writer.WriteEndElement(); // trk
            writer.WriteEndElement(); // gpx
            writer.WriteEndDocument();
        }

        return stringWriter.ToString();
    }

    private static void WriteWaypoint(XmlWriter writer, GeoPoint point, string name)
    {
        writer.WriteStartElement("wpt");
        writer.WriteAttributeString("lat", point.Lat.ToString("F6", CultureInfo.InvariantCulture));
        writer.WriteAttributeString("lon", point.Lon.ToString("F6", CultureInfo.InvariantCulture));
        if (point.Elevation.HasValue)
            writer.WriteElementString("ele", point.Elevation.Value.ToString("F1", CultureInfo.InvariantCulture));
        writer.WriteElementString("name", name);
        writer.WriteElementString("sym", "Flag, Blue");
        writer.WriteEndElement();
    }

    private sealed class Utf8StringWriter : System.IO.StringWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
    }
}
