using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MuPDFCore;

namespace PDFReader.Services;

public sealed class PdfDocumentService : IDisposable
{
    private MuPDFContext? _context;
    private MuPDFDocument? _document;

    public bool IsOpen => _document is not null;
    public int PageCount => _document?.Pages.Count ?? 0;

    public Task OpenAsync(string filePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Close();

        _context = new MuPDFContext(256u * 1024u * 1024u);
        _document = new MuPDFDocument(_context, filePath);
        return Task.CompletedTask;
    }

    public Task<Stream> RenderPageAsync(int pageIndex, double zoom, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_document is null)
        {
            throw new InvalidOperationException("No PDF document is open.");
        }

        if (pageIndex < 0 || pageIndex >= _document.Pages.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        var output = new MemoryStream();
        _document.WriteImage(pageIndex, zoom, PixelFormats.RGB, output, RasterOutputFileTypes.PNG, true);
        output.Position = 0;
        return Task.FromResult<Stream>(output);
    }

    public Task<Stream> RenderPageRegionAsync(
        int pageIndex,
        double x,
        double y,
        double width,
        double height,
        double zoom,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_document is null)
        {
            throw new InvalidOperationException("No PDF document is open.");
        }

        var page = _document.Pages[pageIndex];
        var bounds = page.Bounds;
        var region = new Rectangle(
            bounds.X0 + x / zoom,
            bounds.Y0 + y / zoom,
            bounds.X0 + (x + width) / zoom,
            bounds.Y0 + (y + height) / zoom);

        var output = new MemoryStream();
        _document.WriteImage(pageIndex, region, zoom, PixelFormats.RGB, output, RasterOutputFileTypes.PNG, true);
        output.Position = 0;
        return Task.FromResult<Stream>(output);
    }

    public void Close()
    {
        _document?.Dispose();
        _document = null;
        _context?.Dispose();
        _context = null;
    }

    public void Dispose() => Close();
}
