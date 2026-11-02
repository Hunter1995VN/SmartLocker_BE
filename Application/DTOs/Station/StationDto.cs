namespace Application.DTOs.Station;

public class StationDto
{
    public Guid Id { get; set; }
    public double? RatingAverage { get; set; }
    public int RatingCount { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public decimal Latitude { get; set; }
    public decimal Longitude { get; set; }
    public string Status { get; set; } = string.Empty;
    public string OpensAt { get; set; } = string.Empty;
    public string ClosesAt { get; set; } = string.Empty;
    public short TotalS { get; set; }
    public short TotalM { get; set; }
    public short TotalL { get; set; }
    public string? ContactPhone { get; set; }

    // Availability (calculated)
    public int AvailableS { get; set; }
    public int AvailableM { get; set; }
    public int AvailableL { get; set; }

    // Pricing & Policies from Database
    public decimal? PriceS { get; set; }
    public decimal? PriceM { get; set; }
    public decimal? PriceL { get; set; }
    public decimal? OverdueFeeS { get; set; }
    public decimal? OverdueFeeM { get; set; }
    public decimal? OverdueFeeL { get; set; }
    public short MinHours { get; set; } = 1;
    public short MaxHours { get; set; } = 72;
    public short GracePeriodMinutes { get; set; } = 15;
    public short RefundCutoffHours { get; set; } = 2;
    public decimal RefundRateEarly { get; set; } = 1.00m;
    public decimal RefundRateLate { get; set; } = 0.50m;
    public List<StationSizePricingDto> SizePricing { get; set; } = [];
    public List<StationLockerDto> Lockers { get; set; } = [];
}

public class StationSizePricingDto
{
    public string Size { get; set; } = "";
    public decimal PricePerBlock { get; set; }
    public short BlockHours { get; set; }
    public short MinHours { get; set; }
    public short MaxHours { get; set; }
}

public class StationLockerDto
{
    public Guid LockerId { get; set; }
    public string LockerCode { get; set; } = "";
    public string Size { get; set; } = "";
    public string BusinessStatus { get; set; } = "";
    public string HealthStatus { get; set; } = "";
    public string DoorState { get; set; } = "UNKNOWN";
    public string ConnectivityStatus { get; set; } = "OFFLINE";
    public DateTime? LastDoorEventAt { get; set; }
}
