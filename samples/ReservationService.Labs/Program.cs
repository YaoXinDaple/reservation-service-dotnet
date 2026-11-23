using Cassandra;
using ReservationService.Cassandra;
using ReservationService.Core;
using ReservationService.Labs;

var command = args.FirstOrDefault() ?? "all";
var commands = new[] { "prepared", "paging", "ttl", "lwt", "batch", "schema-exists",
    "anti-pattern", "sai", "tracing", "tombstones", "consistency",
    "token", "overwrite", "lww", "static-counter", "report-diff" };
if (command != "all" && !commands.Contains(command) && command is not ("if-not-exists" or "if-exists"))
{
    Console.Error.WriteLine("Usage: dotnet run --project samples/ReservationService.Labs -- [all|prepared|paging|ttl|lwt|if-not-exists|if-exists|batch|schema-exists|anti-pattern|sai|tracing|tombstones|consistency|token|overwrite|lww|static-counter|report-diff]");
    return 1;
}

var options = new CassandraOptions
{
    ContactPoints = [Environment.GetEnvironmentVariable("Cassandra__ContactPoints__0") ?? "127.0.0.1"],
    Port = int.Parse(Environment.GetEnvironmentVariable("Cassandra__Port") ?? "9042"),
    LocalDatacenter = Environment.GetEnvironmentVariable("Cassandra__LocalDatacenter") ?? "datacenter1",
    Keyspace = "reservation_labs"
};
using var database = new CassandraDatabase(options);
await database.InitializeAsync();
var session = database.Session;
await session.ExecuteAsync(new SimpleStatement("CREATE TABLE IF NOT EXISTS lab_items (bucket text, id int, value text, PRIMARY KEY (bucket, id))"));
await session.ExecuteAsync(new SimpleStatement("CREATE TABLE IF NOT EXISTS lab_expirations (id uuid PRIMARY KEY, value text)"));
var insert = await session.PrepareAsync("INSERT INTO lab_items (bucket, id, value) VALUES (?, ?, ?)");
var select = await session.PrepareAsync("SELECT id, value FROM lab_items WHERE bucket = ?");
var conditionalReservations = await ConditionalReservationExamples.CreateAsync(session);
var queryLabs = new QueryLabs(session, options.LocalDatacenter);
var modelingLabs = new ModelingLabs(session);
var reportDiffLab = new ReportDiffLab(session);

foreach (var lab in command == "all" ? commands : [command])
{
    Console.WriteLine($"\n=== {lab} ===");
    switch (lab)
    {
        case "prepared": await Prepared(); break;
        case "paging": await Paging(); break;
        case "ttl": await Ttl(); break;
        case "lwt": await Lwt(); break;
        case "if-not-exists": await IfNotExists(); break;
        case "if-exists": await IfExists(); break;
        case "batch": await Batch(); break;
        case "schema-exists": await SchemaExists(); break;
        case "anti-pattern": await queryLabs.AntiPattern(); break;
        case "sai": await queryLabs.Sai(); break;
        case "tracing": await queryLabs.Tracing(); break;
        case "tombstones": await queryLabs.Tombstones(); break;
        case "consistency": await queryLabs.Consistency(); break;
        case "token": await modelingLabs.Token(); break;
        case "overwrite": await modelingLabs.Overwrite(); break;
        case "lww": await modelingLabs.LastWriteWins(); break;
        case "static-counter": await modelingLabs.StaticAndCounter(); break;
        case "report-diff": await reportDiffLab.Run(); break;
    }
    Console.WriteLine("PASS");
}
return 0;

async Task Prepared()
{
    var bucket = Guid.NewGuid().ToString();
    // Prepared once above; each Bind supplies new business values without CQL interpolation.
    await session.ExecuteAsync(insert.Bind(bucket, 1, "first"));
    await session.ExecuteAsync(insert.Bind(bucket, 2, "O'Reilly"));
    var rows = (await session.ExecuteAsync(select.Bind(bucket))).ToList();
    Require(rows.Count == 2 && rows[1].GetValue<string>("value") == "O'Reilly", "Prepared values did not round-trip.");
    Console.WriteLine("同一 PreparedStatement 绑定两次；带单引号的值按原样读回。");
    await DeleteBucket(bucket);
}

async Task Paging()
{
    var bucket = Guid.NewGuid().ToString();
    for (var i = 0; i < 7; i++) await session.ExecuteAsync(insert.Bind(bucket, i, $"item-{i}"));
    byte[]? state = null;
    var ids = new List<int>();
    var pages = 0;
    do
    {
        Require(++pages <= 20, "Paging failed to terminate.");
        var bound = select.Bind(bucket).SetAutoPage(false).SetPageSize(3);
        if (state is not null) bound.SetPagingState(state);
        var rows = await session.ExecuteAsync(bound);
        var current = rows.Select(row => row.GetValue<int>("id")).ToList();
        ids.AddRange(current);
        state = rows.PagingState;
        Console.WriteLine($"本页 {current.Count} 条，存在后续游标：{state is not null}");
    } while (state is not null);
    Require(ids.SequenceEqual(Enumerable.Range(0, 7)), "Paging lost or duplicated rows.");
    Console.WriteLine("驱动原始 PagingState 只在进程内传递；API 会保护游标并绑定查询条件。");
    await DeleteBucket(bucket);
}

async Task Ttl()
{
    var id = Guid.NewGuid();
    var write = await session.PrepareAsync("INSERT INTO lab_expirations (id, value) VALUES (?, ?) USING TTL 5");
    var read = await session.PrepareAsync("SELECT value, TTL(value) AS remaining FROM lab_expirations WHERE id = ?");
    await session.ExecuteAsync(write.Bind(id, "expires"));
    var first = (await session.ExecuteAsync(read.Bind(id))).SingleOrDefault();
    Require(first is not null, "TTL row was not created.");
    Console.WriteLine($"初次读取剩余秒数：{first!.GetValue<int>("remaining")}");
    var expired = false;
    for (var i = 0; i < 15; i++)
    {
        await Task.Delay(1000);
        if (!(await session.ExecuteAsync(read.Bind(id))).Any()) { expired = true; break; }
    }
    Require(expired, "TTL row did not expire within the observation window.");
    Console.WriteLine("TTL 到期后查询不到该行。磁盘空间回收由后续 compaction 决定。");
}

async Task Lwt()
{
    await IfNotExists();
    await IfExists();

    var reservation = NewLabReservation();
    try
    {
        var contenders = Enumerable.Range(0, 8).Select(_ => reservation with
            { ConfirmationNumber = Guid.NewGuid(), GuestId = Guid.NewGuid() }).ToArray();
        var attempts = await Task.WhenAll(contenders.Select(conditionalReservations.InsertIfNotExistsAsync));
        Require(attempts.Count(applied => applied) == 1, "LWT must allow exactly one reservation for this room and arrival date.");
        var winner = contenders[Array.FindIndex(attempts, applied => applied)];
        Require(await FindLabReservation(reservation) == winner, "Stored reservation must match the successful contender.");
        Console.WriteLine("同酒店、入住日、房号的 8 个并发条件插入，恰好 1 个 [applied]=true，保存的是获胜者的完整预订。");
    }
    finally { await DeleteLabReservation(reservation); }
    Console.WriteLine("LWT 处理单分区条件写入；无法直接包装主业务的两张表成为跨表事务。");
}

async Task IfNotExists()
{
    var reservation = NewLabReservation();
    try
    {
        var first = await conditionalReservations.InsertIfNotExistsAsync(reservation);
        var duplicate = await conditionalReservations.InsertIfNotExistsAsync(reservation with
            { ConfirmationNumber = Guid.NewGuid(), GuestId = Guid.NewGuid(), EndDate = reservation.EndDate.AddDays(1) });
        Require(first && !duplicate, "First insert must apply and a duplicate key must be rejected.");
        Require(await FindLabReservation(reservation) == reservation, "Rejected insert must not overwrite the original reservation.");
        Console.WriteLine($"INSERT IF NOT EXISTS：首次 [applied]={first}，重复 [applied]={duplicate}；原预订未被覆盖。");
    }
    finally { await DeleteLabReservation(reservation); }
}

async Task IfExists()
{
    var reservation = NewLabReservation();
    try
    {
        var absentUpdate = await UpdateLabReservation(reservation);
        Require(!absentUpdate && await FindLabReservation(reservation) is null,
            "UPDATE IF EXISTS must not create a missing reservation.");
        Require(await conditionalReservations.InsertIfNotExistsAsync(reservation), "Setup insert must apply.");
        var changed = reservation with { GuestId = Guid.NewGuid(), EndDate = reservation.EndDate.AddDays(2) };
        var presentUpdate = await UpdateLabReservation(changed);
        Require(presentUpdate && await FindLabReservation(reservation) == changed,
            "UPDATE IF EXISTS must update an existing reservation while retaining its confirmation number.");
        var presentDelete = await DeleteLabReservation(reservation);
        var absentDelete = await DeleteLabReservation(reservation);
        Require(presentDelete && !absentDelete && await FindLabReservation(reservation) is null,
            "DELETE IF EXISTS must distinguish existing and missing reservations.");
        Console.WriteLine($"UPDATE IF EXISTS：不存在时 [applied]={absentUpdate}，存在时 [applied]={presentUpdate}。");
        Console.WriteLine($"DELETE IF EXISTS：存在时 [applied]={presentDelete}，再次删除 [applied]={absentDelete}。");
    }
    finally { await DeleteLabReservation(reservation); }
}

Task<Reservation?> FindLabReservation(Reservation reservation) => conditionalReservations.FindAsync(
    reservation.HotelId, reservation.StartDate, reservation.RoomNumber);

Task<bool> UpdateLabReservation(Reservation reservation) => conditionalReservations.UpdateIfExistsAsync(
    reservation.HotelId, reservation.StartDate, reservation.RoomNumber, reservation.EndDate, reservation.GuestId);

Task<bool> DeleteLabReservation(Reservation reservation) => conditionalReservations.DeleteIfExistsAsync(
    reservation.HotelId, reservation.StartDate, reservation.RoomNumber);

static Reservation NewLabReservation() => new(Guid.NewGuid(), "H_LAB_" + Guid.NewGuid().ToString("N"),
    new DateOnly(2026, 11, 6), new DateOnly(2026, 11, 9), 101, Guid.NewGuid());

async Task SchemaExists()
{
    // Only this generated sandbox keyspace is dropped; never the configured API/lab keyspace.
    var keyspace = "rs_schema_" + Guid.NewGuid().ToString("N");
    var create = new SimpleStatement($$"""
        CREATE KEYSPACE IF NOT EXISTS {{keyspace}}
        WITH replication = {'class': 'NetworkTopologyStrategy', '{{options.LocalDatacenter.Replace("'", "''")}}': 1}
        """);
    var exists = await session.PrepareAsync("SELECT keyspace_name FROM system_schema.keyspaces WHERE keyspace_name = ?");
    try
    {
        await session.ExecuteAsync(create);
        await session.ExecuteAsync(create);
        Require((await session.ExecuteAsync(exists.Bind(keyspace))).Single().GetValue<string>("keyspace_name") == keyspace,
            "Repeated CREATE IF NOT EXISTS must leave the keyspace present.");
        Console.WriteLine("CREATE KEYSPACE IF NOT EXISTS 连续执行两次成功，测试 keyspace 存在。");
        await DropSandbox();
        await DropSandbox();
        Require(!(await session.ExecuteAsync(exists.Bind(keyspace))).Any(), "Sandbox keyspace must be absent after DROP.");
        Console.WriteLine("DROP KEYSPACE IF EXISTS 连续执行两次成功，测试 keyspace 已删除。这是 schema 操作，不返回 LWT [applied]。");
    }
    finally { await DropSandbox(); }

    async Task DropSandbox() => await session.ExecuteAsync(new SimpleStatement($"DROP KEYSPACE IF EXISTS {keyspace}"));
}

// Implemented in BatchLab.cs: half-finished write, LOGGED BATCH, and two misuses with server warnings.
Task Batch() => new BatchLab(session).Run();

async Task DeleteBucket(string bucket)
{
    var delete = await session.PrepareAsync("DELETE FROM lab_items WHERE bucket = ?");
    await session.ExecuteAsync(delete.Bind(bucket));
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
