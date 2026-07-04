using ErsatzTV.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErsatzTV.Infrastructure.Data.Configurations;

public class NavidromeLibraryConfiguration : IEntityTypeConfiguration<NavidromeLibrary>
{
    public void Configure(EntityTypeBuilder<NavidromeLibrary> builder)
    {
        builder.ToTable("NavidromeLibrary");

        builder.Property(l => l.ItemId)
            .HasMaxLength(36)
            .IsUnicode(false);

        builder.HasIndex(l => l.ItemId);
    }
}
