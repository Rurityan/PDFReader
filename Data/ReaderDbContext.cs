using Microsoft.EntityFrameworkCore;
using PDFReader.Models;

namespace PDFReader.Data;

public sealed class ReaderDbContext : DbContext
{
    public ReaderDbContext(DbContextOptions<ReaderDbContext> options) : base(options)
    {
    }

    public DbSet<OcrRecord> OcrRecords => Set<OcrRecord>();
    public DbSet<Bookmark> Bookmarks => Set<Bookmark>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OcrRecord>(entity =>
        {
            entity.HasKey(record => record.Id);
            entity.Property(record => record.DocumentPath).IsRequired();
            entity.Property(record => record.Text).IsRequired();
            entity.HasIndex(record => new { record.DocumentPath, record.PageNumber });
        });

        modelBuilder.Entity<Bookmark>(entity =>
        {
            entity.HasKey(bookmark => bookmark.Id);
            entity.Property(bookmark => bookmark.DocumentPath).IsRequired();
            entity.Property(bookmark => bookmark.Title).IsRequired();
            entity.HasIndex(bookmark => new { bookmark.DocumentPath, bookmark.PageNumber });
        });
    }
}
