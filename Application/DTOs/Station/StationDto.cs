namespace Application.DTOs.Station;

public class StationDto
{
    public Guid Id { get; set; }
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

    // Pricing
    public decimal? PriceS { get; set; }
    public decimal? PriceM { get; set; }
    public decimal? PriceL { get; set; }
}
