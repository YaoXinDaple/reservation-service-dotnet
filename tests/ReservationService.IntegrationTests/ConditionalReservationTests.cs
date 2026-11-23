using Microsoft.Extensions.DependencyInjection;
using ReservationService.Cassandra;
using ReservationService.Core;
using ReservationService.Labs;

namespace ReservationService.IntegrationTests;

public sealed class ConditionalReservationTests(CassandraApiFixture fixture)
    : IClassFixture<CassandraApiFixture>, IAsyncLifetime
{
    private ConditionalReservationExamples _examples = null!;

    public async Task InitializeAsync() => _examples = await ConditionalReservationExamples.CreateAsync(
        fixture.Services.GetRequiredService<CassandraDatabase>().Session);

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task If_not_exists_rejects_duplicate_room_without_overwriting_original_data()
    {
        var original = NewReservation();
        Assert.True(await _examples.InsertIfNotExistsAsync(original));
        var duplicate = original with { ConfirmationNumber = Guid.NewGuid(), GuestId = Guid.NewGuid(), EndDate = original.EndDate.AddDays(2) };
        Assert.False(await _examples.InsertIfNotExistsAsync(duplicate));
        Assert.Equal(original, await FindAsync(original));
    }

    [Fact]
    public async Task If_not_exists_allows_different_room_or_arrival_date()
    {
        var first = NewReservation();
        var second = first with { ConfirmationNumber = Guid.NewGuid(), RoomNumber = 102 };
        var third = first with { ConfirmationNumber = Guid.NewGuid(), StartDate = first.StartDate.AddDays(1) };
        foreach (var reservation in new[] { first, second, third })
        {
            Assert.True(await _examples.InsertIfNotExistsAsync(reservation));
            Assert.Equal(reservation, await FindAsync(reservation));
        }
    }

    [Fact]
    public async Task Update_if_exists_changes_details_and_retains_confirmation_number()
    {
        var original = NewReservation();
        Assert.True(await _examples.InsertIfNotExistsAsync(original));
        var changed = original with { EndDate = original.EndDate.AddDays(2), GuestId = Guid.NewGuid() };
        Assert.True(await _examples.UpdateIfExistsAsync(changed.HotelId, changed.StartDate,
            changed.RoomNumber, changed.EndDate, changed.GuestId));
        Assert.Equal(changed, await FindAsync(original));
    }

    [Fact]
    public async Task Update_if_exists_does_not_upsert_a_missing_reservation()
    {
        var missing = NewReservation();
        Assert.False(await _examples.UpdateIfExistsAsync(missing.HotelId, missing.StartDate,
            missing.RoomNumber, missing.EndDate, missing.GuestId));
        Assert.Null(await FindAsync(missing));
    }

    [Fact]
    public async Task Delete_if_exists_reports_whether_a_reservation_was_present()
    {
        var reservation = NewReservation();
        Assert.True(await _examples.InsertIfNotExistsAsync(reservation));
        Assert.True(await _examples.DeleteIfExistsAsync(reservation.HotelId, reservation.StartDate, reservation.RoomNumber));
        Assert.Null(await FindAsync(reservation));
        Assert.False(await _examples.DeleteIfExistsAsync(reservation.HotelId, reservation.StartDate, reservation.RoomNumber));
        Assert.Null(await FindAsync(reservation));
    }

    [Fact]
    public async Task Concurrent_conditional_inserts_store_exactly_one_winners_complete_reservation()
    {
        var reservation = NewReservation();
        var contenders = Enumerable.Range(0, 8).Select(_ => reservation with
            { ConfirmationNumber = Guid.NewGuid(), GuestId = Guid.NewGuid() }).ToArray();
        var applied = await Task.WhenAll(contenders.Select(_examples.InsertIfNotExistsAsync));
        Assert.Single(applied, result => result);
        var winner = contenders[Array.FindIndex(applied, result => result)];
        Assert.Equal(winner, await FindAsync(reservation));
    }

    private Task<Reservation?> FindAsync(Reservation reservation) =>
        _examples.FindAsync(reservation.HotelId, reservation.StartDate, reservation.RoomNumber);

    private static Reservation NewReservation() => new(Guid.NewGuid(), "H_TEST_" + Guid.NewGuid().ToString("N"),
        new DateOnly(2026, 11, 6), new DateOnly(2026, 11, 9), 101, Guid.NewGuid());
}
