using Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace API.Controllers.Admin;

[ApiController, Authorize(Roles = "ADMIN,STAFF"), Route("api/admin/traveler-support")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class TravelerSupportAdminController(ISmartLockerDbContext db) : ControllerBase
{
    [HttpGet("ratings")]
    public async Task<IActionResult> Ratings() => Ok(await db.StationReviews
        .GroupBy(x => new { x.Booking.StationId, StationName = x.Booking.Station.Name })
        .Select(g => new { stationId = g.Key.StationId, stationName = g.Key.StationName,
            count = g.Count(), average = g.Average(x => x.Stars) }).ToListAsync());
}
