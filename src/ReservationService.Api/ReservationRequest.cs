using System.ComponentModel.DataAnnotations;
using ReservationService.Core;

namespace ReservationService.Api;

public sealed class ReservationRequest : IValidatableObject
{
    [Required, StringLength(64)]
    public string HotelId { get; init; } = "";

    [Required]
    public DateOnly? StartDate { get; init; }

    [Required]
    public DateOnly? EndDate { get; init; }

    [Required, Range(1, short.MaxValue)]
    public short? RoomNumber { get; init; }

    [Required]
    public Guid? GuestId { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (StartDate.HasValue && EndDate.HasValue && EndDate <= StartDate)
            yield return new ValidationResult("离店日期必须晚于入住日期。", [nameof(EndDate)]);
        if (GuestId == Guid.Empty)
            yield return new ValidationResult("GuestId 不能是空 GUID。", [nameof(GuestId)]);
    }

    public ReservationDetails ToDetails() => new(HotelId.Trim(), StartDate!.Value, EndDate!.Value, RoomNumber!.Value, GuestId!.Value);
}
