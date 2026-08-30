using System.Globalization;
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

    private sealed class Utf8StringWriter : System.IO.StringWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
    }
}
