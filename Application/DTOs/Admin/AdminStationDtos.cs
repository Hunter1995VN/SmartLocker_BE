namespace Application.DTOs.Admin;

using Domain.Enums;

public class CreateStationRequest
{
    public string Name { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public decimal Latitude { get; set; }
    public decimal Longitude { get; set; }
    public string OpensAt { get; set; } = "06:00";
    public string ClosesAt { get; set; } = "22:00";
    public string? ContactPhone { get; set; }
    public short TotalS { get; set; }
    public short TotalM { get; set; }
    public short TotalL { get; set; }
    public decimal PriceSPerBlock { get; set; } = 10000;
    public decimal PriceMPerBlock { get; set; } = 15000;
    public decimal PriceLPerBlock { get; set; } = 20000;
}

public class UpdateStationRequest
{
    public string Name { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public decimal Latitude { get; set; }
    public decimal Longitude { get; set; }
    public StationStatus Status { get; set; }
    public string OpensAt { get; set; } = "06:00";
    public string ClosesAt { get; set; } = "22:00";
    public string? ContactPhone { get; set; }
}

public class StationAdminDetailDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public decimal Latitude { get; set; }
    public decimal Longitude { get; set; }
    public string Status { get; set; } = string.Empty;
    public string OpensAt { get; set; } = string.Empty;
    public string ClosesAt { get; set; } = string.Empty;
    public string? ContactPhone { get; set; }
    public short TotalS { get; set; }
    public short TotalM { get; set; }
    public short TotalL { get; set; }
    public int AvailableS { get; set; }
    public int AvailableM { get; set; }
    public int AvailableL { get; set; }
    public decimal? PriceS { get; set; }
    public decimal? PriceM { get; set; }
    public decimal? PriceL { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class UpdatePricingPolicyRequest
{
    public LockerSize Size { get; set; }
    public decimal PricePerBlock { get; set; }
    public short BlockHours { get; set; } = 1;
    public decimal OverdueFeePerHour { get; set; }
    public short GracePeriodMinutes { get; set; } = 15;
}
