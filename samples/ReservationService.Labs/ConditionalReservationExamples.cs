using Cassandra;
using ReservationService.Core;

namespace ReservationService.Labs;

// Mirrors the original LWT exercise's key: ((hotel_id, start_date), room_number).
// Kept in a separate lab table because the main API deliberately allows overlaps.
public sealed class ConditionalReservationExamples
{
    private readonly ISession _session;
    private readonly PreparedStatement _insertIfNotExists;
    private readonly PreparedStatement _updateIfExists;
    private readonly PreparedStatement _deleteIfExists;
    private readonly PreparedStatement _find;

    private ConditionalReservationExamples(ISession session, PreparedStatement insertIfNotExists,
        PreparedStatement updateIfExists, PreparedStatement deleteIfExists, PreparedStatement find)
    {
        _session = session;
        _insertIfNotExists = insertIfNotExists;
        _updateIfExists = updateIfExists;
        _deleteIfExists = deleteIfExists;
        _find = find;
    }

    public static async Task<ConditionalReservationExamples> CreateAsync(ISession session)
    {
        await session.ExecuteAsync(new SimpleStatement("""
            CREATE TABLE IF NOT EXISTS lab_reservations_by_hotel_date (
                hotel_id text,
                start_date date,
                room_number smallint,
                confirmation_number text,
                end_date date,
                guest_id uuid,
                PRIMARY KEY ((hotel_id, start_date), room_number)
            )
            """));

        // Prepare once, then Bind per operation, as in the Java exercise.
        var insert = await session.PrepareAsync("""
            INSERT INTO lab_reservations_by_hotel_date
                (hotel_id, start_date, room_number, confirmation_number, end_date, guest_id)
            VALUES (?, ?, ?, ?, ?, ?) IF NOT EXISTS
            """);
        var update = await session.PrepareAsync("""
            UPDATE lab_reservations_by_hotel_date
            SET end_date = ?, guest_id = ?
            WHERE hotel_id = ? AND start_date = ? AND room_number = ? IF EXISTS
            """);
        var delete = await session.PrepareAsync("""
            DELETE FROM lab_reservations_by_hotel_date
            WHERE hotel_id = ? AND start_date = ? AND room_number = ? IF EXISTS
            """);
        var find = await session.PrepareAsync("""
            SELECT confirmation_number, hotel_id, start_date, end_date, room_number, guest_id
            FROM lab_reservations_by_hotel_date
            WHERE hotel_id = ? AND start_date = ? AND room_number = ?
            """);
        return new ConditionalReservationExamples(session, insert, update, delete, find);
    }

    public Task<bool> InsertIfNotExistsAsync(Reservation reservation) => ExecuteConditionalAsync(
        _insertIfNotExists.Bind(reservation.HotelId, ToLocalDate(reservation.StartDate), reservation.RoomNumber,
            reservation.ConfirmationNumber.ToString(), ToLocalDate(reservation.EndDate), reservation.GuestId));

    // Primary key and confirmation number stay fixed; only non-key details are updated.
    public Task<bool> UpdateIfExistsAsync(string hotelId, DateOnly startDate, short roomNumber,
        DateOnly endDate, Guid guestId) => ExecuteConditionalAsync(
        _updateIfExists.Bind(ToLocalDate(endDate), guestId, hotelId, ToLocalDate(startDate), roomNumber));

    public Task<bool> DeleteIfExistsAsync(string hotelId, DateOnly startDate, short roomNumber) =>
        ExecuteConditionalAsync(_deleteIfExists.Bind(hotelId, ToLocalDate(startDate), roomNumber));

    public async Task<Reservation?> FindAsync(string hotelId, DateOnly startDate, short roomNumber)
    {
        var rows = await _session.ExecuteAsync(_find.Bind(hotelId, ToLocalDate(startDate), roomNumber));
        var row = rows.SingleOrDefault();
        return row is null ? null : new Reservation(
            Guid.Parse(row.GetValue<string>("confirmation_number")), row.GetValue<string>("hotel_id"),
            ToDateOnly(row.GetValue<LocalDate>("start_date")), ToDateOnly(row.GetValue<LocalDate>("end_date")),
            row.GetValue<short>("room_number"), row.GetValue<Guid>("guest_id"));
    }

    private async Task<bool> ExecuteConditionalAsync(BoundStatement statement)
    {
        statement.SetSerialConsistencyLevel(ConsistencyLevel.LocalSerial);
        var rows = await _session.ExecuteAsync(statement);
        // A non-applied conditional write is a normal result, not necessarily an exception.
        return rows.Single().GetValue<bool>("[applied]");
    }

    private static LocalDate ToLocalDate(DateOnly value) => new(value.Year, value.Month, value.Day);
    private static DateOnly ToDateOnly(LocalDate value) => new(value.Year, value.Month, value.Day);
}
