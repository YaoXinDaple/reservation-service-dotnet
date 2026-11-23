namespace ReservationService.Core;

public sealed record Reservation(
    Guid ConfirmationNumber,
    string HotelId,
    DateOnly StartDate,
    DateOnly EndDate,
    short RoomNumber,
    Guid GuestId);

public sealed record ReservationDetails(
    string HotelId, DateOnly StartDate, DateOnly EndDate, short RoomNumber, Guid GuestId);

// PagingState stays inside the server. The API protects it before returning a cursor.
public sealed record ReservationPage(IReadOnlyList<Reservation> Items, byte[]? PagingState);
