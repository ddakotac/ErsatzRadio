using ErsatzTV.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErsatzTV.Infrastructure.Data.Configurations;

public class NavidromeSongConfiguration : IEntityTypeConfiguration<NavidromeSong>
{
    public void Configure(EntityTypeBuilder<NavidromeSong> builder)
    {
        builder.ToTable("NavidromeSong");

        builder.Property(s => s.Etag)
            .HasMaxLength(40)
            .IsUnicode(false);

        builder.Property(s => s.ItemId)
            .HasMaxLength(36)
            .IsUnicode(false);

        builder.HasIndex(s => s.ItemId);
    }
}
