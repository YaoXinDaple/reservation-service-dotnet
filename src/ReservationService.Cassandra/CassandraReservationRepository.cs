using Cassandra;
using ReservationService.Core;

namespace ReservationService.Cassandra;

public sealed class CassandraReservationRepository(CassandraDatabase database) : IReservationRepository, IDisposable
{
    private const string Columns = "confirmation_number, hotel_id, start_date, end_date, room_number, guest_id";
    private readonly SemaphoreSlim _mutations = new(1, 1);
    private PreparedStatement _find = null!;
    private PreparedStatement _list = null!;
    private PreparedStatement _byHotel = null!;
    private PreparedStatement _insertConfirmation = null!;
    private PreparedStatement _insertHotel = null!;
    private PreparedStatement _deleteConfirmation = null!;
    private PreparedStatement _deleteHotel = null!;

    public async Task InitializeAsync()
    {
        var session = database.Session;
        _find = await session.PrepareAsync($"SELECT {Columns} FROM reservations_by_confirmation WHERE confirmation_number = ?");
        _list = await session.PrepareAsync($"SELECT {Columns} FROM reservations_by_confirmation");
        _byHotel = await session.PrepareAsync($"SELECT {Columns} FROM reservations_by_hotel_date WHERE hotel_id = ? AND start_date = ?");
        _insertConfirmation = await session.PrepareAsync($"INSERT INTO reservations_by_confirmation ({Columns}) VALUES (?, ?, ?, ?, ?, ?)");
        _insertHotel = await session.PrepareAsync($"INSERT INTO reservations_by_hotel_date ({Columns}) VALUES (?, ?, ?, ?, ?, ?)");
        _deleteConfirmation = await session.PrepareAsync("DELETE FROM reservations_by_confirmation WHERE confirmation_number = ?");
        _deleteHotel = await session.PrepareAsync("DELETE FROM reservations_by_hotel_date WHERE hotel_id = ? AND start_date = ? AND room_number = ? AND confirmation_number = ?");
    }

    public async Task<Reservation?> FindAsync(Guid confirmationNumber)
    {
        var rows = await database.Session.ExecuteAsync(_find.Bind(confirmationNumber.ToString()));
        var row = rows.FirstOrDefault();
        return row is null ? null : Map(row);
    }

    public Task<ReservationPage> ListAsync(int pageSize, byte[]? pagingState) =>
        ReadPageAsync(_list.Bind(), pageSize, pagingState);

    public Task<ReservationPage> FindByHotelAndDateAsync(string hotelId, DateOnly date, int pageSize, byte[]? pagingState) =>
        ReadPageAsync(_byHotel.Bind(hotelId, ToLocalDate(date)), pageSize, pagingState);

    public async Task<Reservation> CreateAsync(ReservationDetails details)
    {
        var reservation = FromDetails(Guid.NewGuid(), details);
        await _mutations.WaitAsync();
        try
        {
            await database.Session.ExecuteAsync(Upserts(reservation));
            return reservation;
        }
        finally { _mutations.Release(); }
    }

    public async Task<Reservation?> UpdateAsync(Guid confirmationNumber, ReservationDetails details)
    {
        // A process-local gate protects read-old-key + write-new-key in this single API instance.
        // It is not a distributed lock, and LOGGED BATCH is not a cross-table SQL transaction.
        await _mutations.WaitAsync();
        try
        {
            var old = await FindAsync(confirmationNumber);
            if (old is null) return null;
            var updated = FromDetails(confirmationNumber, details);
            var batch = Upserts(updated);
            if (old.HotelId != updated.HotelId || old.StartDate != updated.StartDate || old.RoomNumber != updated.RoomNumber)
                batch.Add(DeleteHotel(old));
            await database.Session.ExecuteAsync(batch);
            return updated;
        }
        finally { _mutations.Release(); }
    }

    public async Task<bool> DeleteAsync(Guid confirmationNumber)
    {
        await _mutations.WaitAsync();
        try
        {
            var old = await FindAsync(confirmationNumber);
            if (old is null) return false;
            var batch = new BatchStatement().SetBatchType(BatchType.Logged)
                .Add(_deleteConfirmation.Bind(confirmationNumber.ToString()))
                .Add(DeleteHotel(old));
            await database.Session.ExecuteAsync(batch);
            return true;
        }
        finally { _mutations.Release(); }
    }

    private BatchStatement Upserts(Reservation reservation) => new BatchStatement()
        .SetBatchType(BatchType.Logged)
        .Add(_insertConfirmation.Bind(Values(reservation)))
        .Add(_insertHotel.Bind(Values(reservation)));

    private BoundStatement DeleteHotel(Reservation reservation) => _deleteHotel.Bind(
        reservation.HotelId, ToLocalDate(reservation.StartDate), reservation.RoomNumber, reservation.ConfirmationNumber.ToString());

    private async Task<ReservationPage> ReadPageAsync(BoundStatement statement, int pageSize, byte[]? pagingState)
    {
        // Disable transparent fetching: enumerating this RowSet must consume only one page.
        statement.SetAutoPage(false).SetPageSize(pageSize);
        if (pagingState is not null) statement.SetPagingState(pagingState);
        var rows = await database.Session.ExecuteAsync(statement);
        return new ReservationPage(rows.Select(Map).ToList(), rows.PagingState);
    }

    private static object[] Values(Reservation r) =>
        [r.ConfirmationNumber.ToString(), r.HotelId, ToLocalDate(r.StartDate), ToLocalDate(r.EndDate), r.RoomNumber, r.GuestId];

    private static Reservation FromDetails(Guid id, ReservationDetails d) =>
        new(id, d.HotelId, d.StartDate, d.EndDate, d.RoomNumber, d.GuestId);

    private static LocalDate ToLocalDate(DateOnly value) => new(value.Year, value.Month, value.Day);
    private static DateOnly ToDateOnly(LocalDate value) => new(value.Year, value.Month, value.Day);

    private static Reservation Map(Row row) => new(
        Guid.Parse(row.GetValue<string>("confirmation_number")), row.GetValue<string>("hotel_id"),
        ToDateOnly(row.GetValue<LocalDate>("start_date")), ToDateOnly(row.GetValue<LocalDate>("end_date")),
        row.GetValue<short>("room_number"), row.GetValue<Guid>("guest_id"));

    public void Dispose() => _mutations.Dispose();
}
