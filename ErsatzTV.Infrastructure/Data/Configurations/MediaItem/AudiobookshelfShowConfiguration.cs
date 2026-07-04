using ErsatzTV.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErsatzTV.Infrastructure.Data.Configurations;

public class AudiobookshelfShowConfiguration : IEntityTypeConfiguration<AudiobookshelfShow>
{
    public void Configure(EntityTypeBuilder<AudiobookshelfShow> builder)
    {
        builder.ToTable("AudiobookshelfShow");

        builder.Property(s => s.Etag)
            .HasMaxLength(36)
            .IsUnicode(false);

        builder.Property(s => s.ItemId)
            .HasMaxLength(36)
            .IsUnicode(false);

        builder.HasIndex(s => s.ItemId);
    }
}
