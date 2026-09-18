namespace Application.DTOs.Station;

public class StationListItemDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public decimal Latitude { get; set; }
    public decimal Longitude { get; set; }
    public string Status { get; set; } = string.Empty;
    public short TotalS { get; set; }
    public short TotalM { get; set; }
    public short TotalL { get; set; }
}
