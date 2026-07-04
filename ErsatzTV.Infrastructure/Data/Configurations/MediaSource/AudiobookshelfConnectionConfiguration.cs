using ErsatzTV.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErsatzTV.Infrastructure.Data.Configurations;

public class AudiobookshelfConnectionConfiguration : IEntityTypeConfiguration<AudiobookshelfConnection>
{
    public void Configure(EntityTypeBuilder<AudiobookshelfConnection> builder) =>
        builder.ToTable("AudiobookshelfConnection");
}
