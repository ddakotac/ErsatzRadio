using ErsatzTV.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErsatzTV.Infrastructure.Data.Configurations;

public class NavidromePathReplacementConfiguration : IEntityTypeConfiguration<NavidromePathReplacement>
{
    public void Configure(EntityTypeBuilder<NavidromePathReplacement> builder) =>
        builder.ToTable("NavidromePathReplacement");
}
