using MetadataExtractor;
using MetadataExtractor.Formats.Exif;

namespace NimShare.Api.Services;

/// <summary>
/// v1.10.178 — liest GPS-Koordinaten aus dem EXIF-Header eines Fotos.
/// Wird beim Upload-Complete einmalig aufgerufen und schreibt Lat/Lon in
/// den StorageFile-Datensatz. Die Album-Landing-Karte baut daraus per
/// Leaflet-fitBounds die Ansicht mit passendem Zoom auf.
///
/// Warum MetadataExtractor und nicht Magick.NET: die native ImageMagick-
/// Lib hängt aktuell im Linux-App-Service beim On-Load (siehe
/// ThumbnailService._disabled). MetadataExtractor ist reines Managed C#,
/// parst JPEG-APP1-Segmente und HEIC-BMFF-Boxes selbst.
/// </summary>
public interface IExifGpsReader
{
    /// <summary>
    /// Extrahiert (Lat, Lon) aus dem Blob. Null-Ergebnis heißt: kein EXIF,
    /// keine GPS-Tags, ungültiger Wert, oder unbekanntes Format. Wirft nie —
    /// GPS-Extraktion darf den Upload-Complete nie kaputt machen.
    /// </summary>
    Task<(double Lat, double Lon)?> TryReadAsync(string blobPath, string contentType, CancellationToken ct = default);
}

public class ExifGpsReader : IExifGpsReader
{
    private readonly IBlobStorageService _blobs;
    private readonly ILogger<ExifGpsReader> _log;

    // Reader-Buffer: die meisten iPhone-JPEG/HEIC haben ihre GPS-Tags in
    // den ersten 128–512 KB. Wir laden trotzdem den ganzen Blob — bei
    // 5-15 MB Fotos ist der Speicher-Impact klein und wir vermeiden
    // fragwürdige Range-Reads die je nach Container nicht seek-fähig
    // sind. Album-Uploads sind eh auf 100 MB gedeckelt.
    public ExifGpsReader(IBlobStorageService blobs, ILogger<ExifGpsReader> log)
    {
        _blobs = blobs;
        _log = log;
    }

    public async Task<(double Lat, double Lon)?> TryReadAsync(string blobPath, string contentType, CancellationToken ct = default)
    {
        try
        {
            var ct2 = (contentType ?? "").ToLowerInvariant();
            if (!ct2.StartsWith("image/")) return null;
            if (string.IsNullOrEmpty(blobPath)) return null;

            using var ms = new MemoryStream();
            await _blobs.DownloadToAsync(blobPath, ms, ct);
            ms.Position = 0;

            var directories = ImageMetadataReader.ReadMetadata(ms);
            var gps = directories.OfType<GpsDirectory>().FirstOrDefault();
            if (gps is null) return null;

            var loc = gps.GetGeoLocation();
            if (loc is null || loc.IsZero) return null;
            var lat = loc.Latitude;
            var lon = loc.Longitude;

            // Sanity: MetadataExtractor liefert im Fehlerfall auch NaN oder
            // out-of-range. Weltkoordinaten sind [-90,90] / [-180,180].
            if (double.IsNaN(lat) || double.IsNaN(lon)) return null;
            if (lat < -90 || lat > 90 || lon < -180 || lon > 180) return null;
            return (lat, lon);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "EXIF-GPS-Read fehlgeschlagen für {Blob}", blobPath);
            return null;
        }
    }
}
