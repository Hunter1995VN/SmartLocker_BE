namespace Infrastructure.Data.Configurations;

using Domain.Entities;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class StationConfiguration : IEntityTypeConfiguration<Station>
{
    public void Configure(EntityTypeBuilder<Station> builder)
    {
        builder.ToTable("Stations", tb => tb.HasTrigger("trg_Stations_UpdatedAt"));

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Name).IsRequired().HasMaxLength(100);
        builder.Property(x => x.Address).IsRequired().HasMaxLength(500);
        builder.Property(x => x.Latitude).HasPrecision(10, 7);
        builder.Property(x => x.Longitude).HasPrecision(10, 7);

        builder.Property(x => x.Status)
            .IsRequired()
            .HasMaxLength(15)
            .HasConversion(v => v.ToString(), v => Enum.Parse<StationStatus>(v))
            .HasDefaultValue(StationStatus.ACTIVE);

        builder.Property(x => x.Timezone).IsRequired().HasMaxLength(50).HasDefaultValue("Asia/Ho_Chi_Minh");
        builder.Property(x => x.ContactPhone).HasMaxLength(20);

        builder.Property(x => x.TotalS).HasDefaultValue(0);
        builder.Property(x => x.TotalM).HasDefaultValue(0);
        builder.Property(x => x.TotalL).HasDefaultValue(0);
        
        builder.Property(x => x.CreatedAt).HasDefaultValueSql("GETUTCDATE()");
        builder.Property(x => x.UpdatedAt).HasDefaultValueSql("GETUTCDATE()");
    }
}
