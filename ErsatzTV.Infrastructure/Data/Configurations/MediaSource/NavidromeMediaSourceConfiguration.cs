using ErsatzTV.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErsatzTV.Infrastructure.Data.Configurations;

public class NavidromeMediaSourceConfiguration : IEntityTypeConfiguration<NavidromeMediaSource>
{
    public void Configure(EntityTypeBuilder<NavidromeMediaSource> builder)
    {
        builder.ToTable("NavidromeMediaSource");

        builder.HasMany(s => s.Connections)
            .WithOne(c => c.NavidromeMediaSource)
            .HasForeignKey(c => c.NavidromeMediaSourceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(s => s.PathReplacements)
            .WithOne(r => r.NavidromeMediaSource)
            .HasForeignKey(r => r.NavidromeMediaSourceId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
