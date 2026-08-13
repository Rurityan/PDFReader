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
    public DbSet<PdfDocument> PdfDocuments => Set<PdfDocument>();
    public DbSet<TtsAudioRecord> TtsAudioRecords => Set<TtsAudioRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PdfDocument>(entity =>
        {
            entity.HasKey(document => document.Id);
            entity.Property(document => document.FilePath).IsRequired();
            entity.Property(document => document.Title).IsRequired();
            entity.Property(document => document.IsArchived).HasDefaultValue(false);
            entity.HasIndex(document => document.FilePath).IsUnique();
        });

        modelBuilder.Entity<Bookmark>(entity =>
        {
            entity.HasKey(bookmark => bookmark.Id);
            entity.Property(bookmark => bookmark.Title).IsRequired();
            entity.HasOne(bookmark => bookmark.PdfDocument)
                .WithMany(document => document.Bookmarks)
                .HasForeignKey(bookmark => bookmark.PdfDocumentId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(bookmark => bookmark.Parent)
                .WithMany(bookmark => bookmark.Children)
                .HasForeignKey(bookmark => bookmark.ParentId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(bookmark => new
            {
                bookmark.PdfDocumentId,
                bookmark.ParentId,
                bookmark.SortOrder,
            });
        });

        modelBuilder.Entity<OcrRecord>(entity =>
        {
            entity.HasKey(record => record.Id);
            entity.Property(record => record.CaptureZoom).IsRequired();
            entity.Property(record => record.Title).IsRequired();
            entity.Property(record => record.Text).IsRequired();
            entity.Property(record => record.IsExternalImport).HasDefaultValue(false);
            entity.HasOne(record => record.PdfDocument)
                .WithMany(document => document.OcrRecords)
                .HasForeignKey(record => record.PdfDocumentId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(record => record.Bookmark)
                .WithMany(bookmark => bookmark.OcrRecords)
                .HasForeignKey(record => record.BookmarkId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasIndex(record => new
            {
                record.PdfDocumentId,
                record.PageNumber,
                record.CreatedAtUtc,
            });
        });

        modelBuilder.Entity<TtsAudioRecord>(entity =>
        {
            entity.HasKey(audio => audio.Id);
            entity.Property(audio => audio.FilePath).IsRequired();
            entity.HasOne(audio => audio.OcrRecord)
                .WithMany(record => record.TtsAudios)
                .HasForeignKey(audio => audio.OcrRecordId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(audio => audio.OcrRecordId);
        });
    }
}
