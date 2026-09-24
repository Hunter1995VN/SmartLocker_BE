namespace Infrastructure.Data.Configurations;

using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class IoTDeviceConfiguration : IEntityTypeConfiguration<IoTDevice>
{
    public void Configure(EntityTypeBuilder<IoTDevice> builder)
    {
        builder.ToTable("IoTDevices", tb => tb.HasTrigger("trg_IoTDevices_UpdatedAt"));

        builder.HasKey(x => x.Id);

        builder.Property(x => x.DeviceCode).IsRequired().HasMaxLength(100);
        builder.Property(x => x.DeviceSecretHash).HasMaxLength(255);
        builder.Property(x => x.DeviceType).HasMaxLength(10);
        builder.Property(x => x.MqttClientId).HasMaxLength(100);
        builder.Property(x => x.FirmwareVersion).HasMaxLength(50);
        builder.Property(x => x.ConnectivityStatus).HasMaxLength(10).HasDefaultValue("ONLINE");

        builder.HasOne(x => x.Station)
            .WithMany()
            .HasForeignKey(x => x.StationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
