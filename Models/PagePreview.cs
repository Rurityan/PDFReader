using Avalonia.Media.Imaging;

namespace PDFReader.Models;

public sealed class PagePreview : System.IDisposable
{
    public PagePreview(int pageNumber, Bitmap image)
    {
        PageNumber = pageNumber;
        Image = image;
    }

    public int PageNumber { get; }
    public Bitmap Image { get; }

    public void Dispose() => Image.Dispose();
}
