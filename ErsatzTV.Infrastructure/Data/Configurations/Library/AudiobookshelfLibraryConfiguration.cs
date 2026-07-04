using ErsatzTV.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErsatzTV.Infrastructure.Data.Configurations;

public class AudiobookshelfLibraryConfiguration : IEntityTypeConfiguration<AudiobookshelfLibrary>
{
    public void Configure(EntityTypeBuilder<AudiobookshelfLibrary> builder)
    {
        builder.ToTable("AudiobookshelfLibrary");

        builder.Property(l => l.ItemId)
            .HasMaxLength(36)
            .IsUnicode(false);

        builder.HasIndex(l => l.ItemId);
    }
}
