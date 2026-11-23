using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ReservationService.Api;
using ReservationService.Core;

namespace ReservationService.IntegrationTests;

public sealed class ReservationsApiTests(CassandraApiFixture fixture) : IClassFixture<CassandraApiFixture>
{
    private HttpClient Client => fixture.Client;
    private const string Root = "/api/v1/reservations";
    private static readonly DateOnly Arrival = new(2026, 11, 6);

    [Fact]
    public async Task Create_get_update_same_key_and_delete_round_trip()
    {
        var request = Request(NewHotel());
        var created = await CreateAsync(request);
        var found = await Client.GetFromJsonAsync<Reservation>($"{Root}/{created.ConfirmationNumber}");
        Assert.Equal(created, found);

        var changed = Request(request.HotelId, endDate: Arrival.AddDays(5), guestId: Guid.NewGuid());
        var updated = await Client.PutAsJsonAsync($"{Root}/{created.ConfirmationNumber}", changed);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var hotelPage = await HotelPage(request.HotelId);
        var row = Assert.Single(hotelPage.Items);
        Assert.Equal(changed.GuestId, row.GuestId);
        Assert.Equal(changed.EndDate, row.EndDate);

        Assert.Equal(HttpStatusCode.NoContent, (await Client.DeleteAsync($"{Root}/{created.ConfirmationNumber}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync($"{Root}/{created.ConfirmationNumber}")).StatusCode);
        Assert.Empty((await HotelPage(request.HotelId)).Items);
    }

    [Fact]
    public async Task Moving_hotel_date_and_room_removes_the_old_query_row()
    {
        var oldHotel = NewHotel();
        var newHotel = NewHotel();
        var created = await CreateAsync(Request(oldHotel));
        var changed = Request(newHotel, Arrival.AddDays(1), room: 202);
        (await Client.PutAsJsonAsync($"{Root}/{created.ConfirmationNumber}", changed)).EnsureSuccessStatusCode();
        Assert.Empty((await HotelPage(oldHotel)).Items);
        var row = Assert.Single((await HotelPage(newHotel, Arrival.AddDays(1))).Items);
        Assert.Equal(created.ConfirmationNumber, row.ConfirmationNumber);
        Assert.Equal(202, row.RoomNumber);
        Assert.Equal(HttpStatusCode.NoContent, (await Client.DeleteAsync($"{Root}/{created.ConfirmationNumber}")).StatusCode);
        Assert.Empty((await HotelPage(newHotel, Arrival.AddDays(1))).Items);
    }

    [Fact]
    public async Task Moving_only_room_removes_the_old_clustering_key()
    {
        var hotel = NewHotel();
        var created = await CreateAsync(Request(hotel));
        (await Client.PutAsJsonAsync($"{Root}/{created.ConfirmationNumber}", Request(hotel, room: 909))).EnsureSuccessStatusCode();
        var row = Assert.Single((await HotelPage(hotel)).Items);
        Assert.Equal(909, row.RoomNumber);
        Assert.Equal(created.ConfirmationNumber, row.ConfirmationNumber);
    }

    [Fact]
    public async Task Overlapping_reservations_stay_independent_and_query_uses_arrival_date()
    {
        var hotel = NewHotel();
        var first = await CreateAsync(Request(hotel));
        var second = await CreateAsync(Request(hotel));
        var third = await CreateAsync(Request(hotel, Arrival.AddDays(1)));
        var sameDay = (await HotelPage(hotel)).Items;
        Assert.Equal(2, sameDay.Count);
        Assert.Contains(sameDay, r => r.ConfirmationNumber == first.ConfirmationNumber);
        Assert.Contains(sameDay, r => r.ConfirmationNumber == second.ConfirmationNumber);
        Assert.Equal(third.ConfirmationNumber, Assert.Single((await HotelPage(hotel, Arrival.AddDays(1))).Items).ConfirmationNumber);

        (await Client.DeleteAsync($"{Root}/{first.ConfirmationNumber}")).EnsureSuccessStatusCode();
        Assert.Equal(second.ConfirmationNumber, Assert.Single((await HotelPage(hotel)).Items).ConfirmationNumber);
    }

    [Fact]
    public async Task Hotel_cursor_pages_without_duplicates_and_rejects_other_queries_or_tampering()
    {
        var hotel = NewHotel();
        var expected = new HashSet<Guid>();
        for (var i = 0; i < 7; i++) expected.Add((await CreateAsync(Request(hotel))).ConfirmationNumber);
        var first = await HotelPage(hotel, size: 2);
        Assert.NotNull(first.NextCursor);
        var token = Uri.EscapeDataString(first.NextCursor);
        var query = $"{Root}/findByHotelAndDate?hotelId={hotel}&date={Arrival:yyyy-MM-dd}";
        foreach (var invalidUrl in new[]
        {
            $"{query}&pageSize=3&cursor={token}",
            $"{Root}/findByHotelAndDate?hotelId=other&date={Arrival:yyyy-MM-dd}&pageSize=2&cursor={token}",
            $"{Root}/findByHotelAndDate?hotelId={hotel}&date=2026-11-07&pageSize=2&cursor={token}",
            $"{Root}?pageSize=2&cursor={token}",
            $"{query}&pageSize=2&cursor=invalid"
        })
            Assert.Equal(HttpStatusCode.BadRequest, (await Client.GetAsync(invalidUrl)).StatusCode);

        var actual = new List<Guid>();
        ReservationPageResponse page = first;
        var pages = 0;
        while (true)
        {
            Assert.InRange(++pages, 1, 20);
            Assert.InRange(page.Items.Count, 0, 2);
            actual.AddRange(page.Items.Select(r => r.ConfirmationNumber));
            if (page.NextCursor is null) break;
            page = (await Client.GetFromJsonAsync<ReservationPageResponse>(
                $"{query}&pageSize=2&cursor={Uri.EscapeDataString(page.NextCursor)}"))!;
        }
        Assert.Equal(7, actual.Count);
        Assert.True(expected.SetEquals(actual));
    }

    [Fact]
    public async Task All_reservations_support_cursor_browsing()
    {
        var ownIds = new HashSet<Guid>();
        for (var i = 0; i < 4; i++) ownIds.Add((await CreateAsync(Request(NewHotel()))).ConfirmationNumber);
        var seen = new HashSet<Guid>();
        string? cursor = null;
        var pages = 0;
        do
        {
            Assert.InRange(++pages, 1, 100);
            var url = $"{Root}?pageSize=3" + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
            var page = (await Client.GetFromJsonAsync<ReservationPageResponse>(url))!;
            Assert.InRange(page.Items.Count, 0, 3);
            foreach (var row in page.Items) Assert.True(seen.Add(row.ConfirmationNumber));
            cursor = page.NextCursor;
        } while (cursor is not null);
        Assert.True(ownIds.IsSubsetOf(seen));
    }

    [Fact]
    public async Task Missing_records_return_404_and_put_does_not_create()
    {
        var url = $"{Root}/{Guid.NewGuid()}";
        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client.PutAsJsonAsync(url, Request(NewHotel()))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client.DeleteAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync(url)).StatusCode);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"hotelId\":\" \"}")]
    [InlineData("{\"hotelId\":\"H\",\"startDate\":\"2026-11-06\",\"endDate\":\"2026-11-06\",\"roomNumber\":1,\"guestId\":\"a2792706-3c1a-43da-a375-d4cfa471de5e\"}")]
    [InlineData("{\"hotelId\":\"H\",\"startDate\":\"2026-11-06\",\"endDate\":\"2026-11-09\",\"roomNumber\":0,\"guestId\":\"a2792706-3c1a-43da-a375-d4cfa471de5e\"}")]
    [InlineData("{\"hotelId\":\"H\",\"startDate\":\"2026-11-06\",\"endDate\":\"2026-11-09\",\"roomNumber\":1,\"guestId\":\"00000000-0000-0000-0000-000000000000\"}")]
    [InlineData("{\"hotelId\":\"H\",\"startDate\":\"not-a-date\"}")]
    public async Task Invalid_bodies_return_problem_details(string json)
    {
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        var response = await Client.PostAsync(Root, content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("?pageSize=0")]
    [InlineData("?pageSize=101")]
    [InlineData("/not-a-guid")]
    [InlineData("/findByHotelAndDate?hotelId=H")]
    [InlineData("/findByHotelAndDate?date=2026-11-06")]
    [InlineData("/findByHotelAndDate?hotelId=%20&date=2026-11-06")]
    [InlineData("/findByHotelAndDate?hotelId=H&date=invalid")]
    public async Task Invalid_queries_return_400(string suffix) =>
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.GetAsync(Root + suffix)).StatusCode);

    [Fact]
    public async Task Concurrent_updates_in_one_api_instance_leave_only_one_hotel_query_row()
    {
        var created = await CreateAsync(Request(NewHotel()));
        var requests = Enumerable.Range(0, 6).Select(i => Request(NewHotel(), Arrival.AddDays(i))).ToArray();
        var responses = await Task.WhenAll(requests.Select(r => Client.PutAsJsonAsync($"{Root}/{created.ConfirmationNumber}", r)));
        foreach (var response in responses) response.EnsureSuccessStatusCode();
        var final = (await Client.GetFromJsonAsync<Reservation>($"{Root}/{created.ConfirmationNumber}"))!;
        var count = 0;
        foreach (var request in requests)
        {
            var page = await HotelPage(request.HotelId, request.StartDate!.Value);
            count += page.Items.Count;
            if (page.Items.Count > 0) Assert.Equal(final, Assert.Single(page.Items));
        }
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Health_openapi_and_swagger_are_available()
    {
        Assert.Equal("Healthy", await Client.GetStringAsync("/health"));
        using var document = JsonDocument.Parse(await Client.GetStringAsync("/openapi/v1.json"));
        Assert.True(document.RootElement.GetProperty("paths").TryGetProperty(Root, out _));
        Assert.Contains("swagger", await Client.GetStringAsync("/swagger/index.html"), StringComparison.OrdinalIgnoreCase);
    }

    private async Task<Reservation> CreateAsync(ReservationRequest request)
    {
        var response = await Client.PostAsJsonAsync(Root, request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        return (await response.Content.ReadFromJsonAsync<Reservation>())!;
    }

    private async Task<ReservationPageResponse> HotelPage(string hotel, DateOnly? date = null, int size = 20) =>
        (await Client.GetFromJsonAsync<ReservationPageResponse>(
            $"{Root}/findByHotelAndDate?hotelId={hotel}&date={date ?? Arrival:yyyy-MM-dd}&pageSize={size}"))!;

    private static string NewHotel() => "H_" + Guid.NewGuid().ToString("N");

    private static ReservationRequest Request(string hotel, DateOnly? date = null,
        DateOnly? endDate = null, short room = 101, Guid? guestId = null) => new()
    {
        HotelId = hotel, StartDate = date ?? Arrival, EndDate = endDate ?? (date ?? Arrival).AddDays(3),
        RoomNumber = room, GuestId = guestId ?? Guid.NewGuid()
    };
}
