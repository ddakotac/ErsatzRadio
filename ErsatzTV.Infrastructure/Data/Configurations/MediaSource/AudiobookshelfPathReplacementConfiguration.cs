using ErsatzTV.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErsatzTV.Infrastructure.Data.Configurations;

public class AudiobookshelfPathReplacementConfiguration : IEntityTypeConfiguration<AudiobookshelfPathReplacement>
{
    public void Configure(EntityTypeBuilder<AudiobookshelfPathReplacement> builder) =>
        builder.ToTable("AudiobookshelfPathReplacement");
}
