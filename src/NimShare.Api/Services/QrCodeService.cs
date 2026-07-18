using QRCoder;

namespace NimShare.Api.Services;

public interface IQrCodeService
{
    string RenderSvg(string url);
    byte[] RenderPng(string url, int pixelsPerModule = 10);
}

public class QrCodeService : IQrCodeService
{
    public string RenderSvg(string url)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
        var svg = new SvgQRCode(data);
        return svg.GetGraphic(4, "#002854", "#ffffff", drawQuietZones: true);
    }

    public byte[] RenderPng(string url, int pixelsPerModule = 10)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data);
        return png.GetGraphic(pixelsPerModule);
    }
}
