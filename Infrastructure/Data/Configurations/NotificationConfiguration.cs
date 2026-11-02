using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace Infrastructure.Data.Configurations;
public class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> b)
    {
        b.ToTable("Notifications"); b.HasKey(x => x.Id);
        b.Property(x => x.Channel).HasMaxLength(10);
        b.Property(x => x.Status).HasMaxLength(10);
        b.Property(x => x.NotificationType).HasMaxLength(30);
        b.Property(x => x.Title).HasMaxLength(200);
        b.Property(x => x.DeepLink).HasMaxLength(500);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Booking>().WithMany().HasForeignKey(x => x.BookingId).OnDelete(DeleteBehavior.Restrict);
    }
}
