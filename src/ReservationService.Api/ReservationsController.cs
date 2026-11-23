using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using ReservationService.Core;

namespace ReservationService.Api;

public sealed record ReservationPageResponse(IReadOnlyList<Reservation> Items, string? NextCursor);

[ApiController]
[Route("api/v1/reservations")]
public sealed class ReservationsController(IReservationRepository repository, PageCursor cursors) : ControllerBase
{
    /// <summary>演示性全表浏览。游标分页，不提供全局排序或总数。</summary>
    [HttpGet]
    public async Task<ActionResult<ReservationPageResponse>> List(
        [FromQuery, Range(1, 100)] int pageSize = 20, [FromQuery] string? cursor = null)
    {
        const string scope = "all-reservations";
        var page = await repository.ListAsync(pageSize, cursors.Decode(cursor, scope, pageSize));
        return new ReservationPageResponse(page.Items, cursors.Encode(page.PagingState, scope, pageSize));
    }

    /// <summary>查询指定酒店在指定日期开始入住的预订。</summary>
    [HttpGet("findByHotelAndDate")]
    public async Task<ActionResult<ReservationPageResponse>> FindByHotelAndDate(
        [FromQuery, Required, StringLength(64)] string hotelId,
        [FromQuery, Required] DateOnly? date,
        [FromQuery, Range(1, 100)] int pageSize = 20,
        [FromQuery] string? cursor = null)
    {
        hotelId = hotelId.Trim();
        var scope = JsonSerializer.Serialize(new { hotelId, date });
        var page = await repository.FindByHotelAndDateAsync(hotelId, date!.Value, pageSize, cursors.Decode(cursor, scope, pageSize));
        return new ReservationPageResponse(page.Items, cursors.Encode(page.PagingState, scope, pageSize));
    }

    [HttpGet("{confirmationNumber}")]
    public async Task<ActionResult<Reservation>> GetByConfirmation(Guid confirmationNumber)
    {
        var reservation = await repository.FindAsync(confirmationNumber);
        return reservation is null ? NotFound() : Ok(reservation);
    }

    [HttpPost]
    public async Task<ActionResult<Reservation>> Create(ReservationRequest request)
    {
        var reservation = await repository.CreateAsync(request.ToDetails());
        return CreatedAtAction(nameof(GetByConfirmation), new { confirmationNumber = reservation.ConfirmationNumber }, reservation);
    }

    [HttpPut("{confirmationNumber}")]
    public async Task<ActionResult<Reservation>> Update(Guid confirmationNumber, ReservationRequest request)
    {
        var reservation = await repository.UpdateAsync(confirmationNumber, request.ToDetails());
        return reservation is null ? NotFound() : Ok(reservation);
    }

    [HttpDelete("{confirmationNumber}")]
    public async Task<IActionResult> Delete(Guid confirmationNumber) =>
        await repository.DeleteAsync(confirmationNumber) ? NoContent() : NotFound();
}
