using ErsatzTV.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErsatzTV.Infrastructure.Data.Configurations;

public class AudiobookshelfSeasonConfiguration : IEntityTypeConfiguration<AudiobookshelfSeason>
{
    public void Configure(EntityTypeBuilder<AudiobookshelfSeason> builder)
    {
        builder.ToTable("AudiobookshelfSeason");

        builder.Property(s => s.Etag)
            .HasMaxLength(36)
            .IsUnicode(false);

        builder.Property(s => s.ItemId)
            .HasMaxLength(36)
            .IsUnicode(false);

        builder.HasIndex(s => s.ItemId);
    }
}
