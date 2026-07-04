using ErsatzTV.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErsatzTV.Infrastructure.Data.Configurations;

public class AudiobookshelfMediaSourceConfiguration : IEntityTypeConfiguration<AudiobookshelfMediaSource>
{
    public void Configure(EntityTypeBuilder<AudiobookshelfMediaSource> builder)
    {
        builder.ToTable("AudiobookshelfMediaSource");

        builder.HasMany(s => s.Connections)
            .WithOne(c => c.AudiobookshelfMediaSource)
            .HasForeignKey(c => c.AudiobookshelfMediaSourceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(s => s.PathReplacements)
            .WithOne(r => r.AudiobookshelfMediaSource)
            .HasForeignKey(r => r.AudiobookshelfMediaSourceId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
