namespace ReservationService.Core;

public interface IReservationRepository
{
    Task<Reservation?> FindAsync(Guid confirmationNumber);
    Task<ReservationPage> ListAsync(int pageSize, byte[]? pagingState);
    Task<ReservationPage> FindByHotelAndDateAsync(
        string hotelId, DateOnly date, int pageSize, byte[]? pagingState);
    Task<Reservation> CreateAsync(ReservationDetails details);
    Task<Reservation?> UpdateAsync(Guid confirmationNumber, ReservationDetails details);
    Task<bool> DeleteAsync(Guid confirmationNumber);
}
