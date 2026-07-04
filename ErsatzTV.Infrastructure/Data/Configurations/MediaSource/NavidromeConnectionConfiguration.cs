using ErsatzTV.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErsatzTV.Infrastructure.Data.Configurations;

public class NavidromeConnectionConfiguration : IEntityTypeConfiguration<NavidromeConnection>
{
    public void Configure(EntityTypeBuilder<NavidromeConnection> builder) =>
        builder.ToTable("NavidromeConnection");
}
