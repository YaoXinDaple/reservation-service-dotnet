using Cassandra;

namespace ReservationService.Labs;

// Labs that explain what Cassandra does with a query: which queries it rejects,
// how SAI changes that, what tracing reveals, what deletes cost, and how RF limits consistency.
public sealed class QueryLabs(ISession session, string localDatacenter)
{
    private static readonly LocalDate Arrival = new(2026, 11, 6);

    // 1. Queries that do not follow the primary key are rejected unless you opt in to filtering.
    public async Task AntiPattern()
    {
        const string table = "reservations_by_hotel_date";
        const string columns = "start_date, room_number, confirmation_number, guest_id";
        var hotel = "H_DEMO_" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var nextDay = new LocalDate(2026, 11, 7);
        var checkout = new LocalDate(2026, 11, 9);
        var guest = Guid.NewGuid();
        var firstConfirmation = Guid.NewGuid().ToString();

        // 1. Show the real primary key, read from the schema rather than hard-coded.
        var keyColumns = (await session.ExecuteAsync(new SimpleStatement("""
            SELECT column_name, kind, position FROM system_schema.columns
            WHERE keyspace_name = ? AND table_name = ?
            """, session.Keyspace, table)))
            .Where(r => r.GetValue<string>("kind") is "partition_key" or "clustering")
            .OrderBy(r => r.GetValue<string>("kind") == "partition_key" ? 0 : 1).ThenBy(r => r.GetValue<int>("position"))
            .ToList();
        var partitionKey = keyColumns.Where(r => r.GetValue<string>("kind") == "partition_key").Select(r => r.GetValue<string>("column_name")).ToList();
        var clustering = keyColumns.Where(r => r.GetValue<string>("kind") == "clustering").Select(r => r.GetValue<string>("column_name")).ToList();
        Console.WriteLine($"表 {table} 的主键（读自 system_schema.columns）：");
        Console.WriteLine($"  PRIMARY KEY (({string.Join(", ", partitionKey)}), {string.Join(", ", clustering)})");
        Console.WriteLine($"  分区键：{string.Join(" + ", partitionKey)}    聚簇列（按顺序）：{string.Join(" → ", clustering)}");

        // 2. Three rows: two rooms in one partition, one row in another partition (next day).
        var insert = await session.PrepareAsync($"""
            INSERT INTO {table} (hotel_id, start_date, room_number, confirmation_number, end_date, guest_id)
            VALUES (?, ?, ?, ?, ?, ?)
            """);
        await session.ExecuteAsync(insert.Bind(hotel, Arrival, (short)101, firstConfirmation, checkout, guest));
        await session.ExecuteAsync(insert.Bind(hotel, Arrival, (short)102, Guid.NewGuid().ToString(), checkout, Guid.NewGuid()));
        await session.ExecuteAsync(insert.Bind(hotel, nextDay, (short)101, Guid.NewGuid().ToString(), checkout, Guid.NewGuid()));
        Console.WriteLine($"\n测试数据：酒店 {hotel}，{Arrival} 入住 2 笔（101、102 房），{nextDay} 入住 1 笔（101 房）。guest_id {Short(guest)} 是 101 房 {Arrival} 那笔。");
        try
        {
            await RunQuery(1, "只给出部分分区键（缺少 start_date）",
                $"SELECT {columns} FROM {table} WHERE hotel_id = ?", [hotel], expectRows: null);
            await RunQuery(2, "按非主键列 guest_id 查询",
                $"SELECT {columns} FROM {table} WHERE guest_id = ?", [guest], expectRows: null);
            await RunQuery(3, "跳过第一个聚簇列 room_number，直接限定 confirmation_number",
                $"SELECT {columns} FROM {table} WHERE hotel_id = ? AND start_date = ? AND confirmation_number = ?",
                [hotel, Arrival, firstConfirmation], expectRows: null);
            await RunQuery(4, "查询 2 加上 ALLOW FILTERING",
                $"SELECT {columns} FROM {table} WHERE guest_id = ? ALLOW FILTERING", [guest], expectRows: 1);
            Console.WriteLine("        能查到，但 Cassandra 要扫描所有分区再逐行过滤，代价见 tracing 实验。");
            await RunQuery(5, "给出完整分区键",
                $"SELECT {columns} FROM {table} WHERE hotel_id = ? AND start_date = ?", [hotel, Arrival], expectRows: 2);
            Console.WriteLine("        直接定位到一个分区，返回该分区内按聚簇列排好序的 2 行。");
            await RunQuery(6, "完整分区键 + 按顺序限定聚簇列",
                $"SELECT {columns} FROM {table} WHERE hotel_id = ? AND start_date = ? AND room_number = ? AND confirmation_number = ?",
                [hotel, Arrival, (short)101, firstConfirmation], expectRows: 1);
            Console.WriteLine("        和查询 3 条件相同，只是补上了 room_number，就能精确定位到一行。");
        }
        finally
        {
            await session.ExecuteAsync(new SimpleStatement(
                $"DELETE FROM {table} WHERE hotel_id = ? AND start_date IN (?, ?)", hotel, Arrival, nextDay));
        }
    }

    // Prints the CQL with values inlined (for display only; execution still binds parameters),
    // then either the server's rejection or the returned rows.
    private async Task RunQuery(int number, string title, string cql, object[] values, int? expectRows)
    {
        Console.WriteLine($"\n[查询 {number}] {title}");
        Console.WriteLine($"  CQL：{Inline(cql, values)};");
        RowSet rows;
        try
        {
            rows = await session.ExecuteAsync(new SimpleStatement(cql, values));
        }
        catch (InvalidQueryException ex)
        {
            Require(expectRows is null, $"Query {number} should have succeeded: {ex.Message}");
            Console.WriteLine("  结果：被拒绝（InvalidQueryException）");
            Console.WriteLine($"        {ex.Message}");
            return;
        }
        Require(expectRows is not null, $"Query {number} should have been rejected.");
        var list = rows.ToList();
        Console.WriteLine($"  结果：返回 {list.Count} 行");
        foreach (var row in list)
            Console.WriteLine($"        start_date={row.GetValue<LocalDate>("start_date")}  room_number={row.GetValue<short>("room_number")}  " +
                $"confirmation_number={Short(row.GetValue<string>("confirmation_number"))}  guest_id={Short(row.GetValue<Guid>("guest_id"))}");
        Require(list.Count == expectRows, $"Query {number} should return {expectRows} rows but returned {list.Count}.");
    }

    private static string Inline(string cql, object[] values)
    {
        var index = 0;
        return System.Text.RegularExpressions.Regex.Replace(cql, @"\?", _ => values[index++] switch
        {
            string s => $"'{s}'",
            LocalDate d => $"'{d}'",
            Guid g => g.ToString(),
            var other => other.ToString()!
        });
    }

    private static string Short(object id) => id.ToString()![..8] + "…";

    // 2. Cassandra 5.0 Storage-Attached Index: query a non-key column without a second table.
    public async Task Sai()
    {
        await session.ExecuteAsync(new SimpleStatement("""
            CREATE TABLE IF NOT EXISTS lab_reservations_sai (
                confirmation_number text PRIMARY KEY,
                hotel_id text,
                start_date date,
                guest_id uuid
            )
            """));
        await session.ExecuteAsync(new SimpleStatement(
            "CREATE INDEX IF NOT EXISTS lab_reservations_sai_guest ON lab_reservations_sai (guest_id) USING 'sai'"));
        await session.ExecuteAsync(new SimpleStatement(
            "CREATE INDEX IF NOT EXISTS lab_reservations_sai_start ON lab_reservations_sai (start_date) USING 'sai'"));

        var insert = await session.PrepareAsync(
            "INSERT INTO lab_reservations_sai (confirmation_number, hotel_id, start_date, guest_id) VALUES (?, ?, ?, ?)");
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        var ids = new List<string>();
        foreach (var (guest, day) in new[] { (alice, 1), (alice, 10), (alice, 20), (bob, 1), (bob, 15) })
        {
            var id = Guid.NewGuid().ToString();
            ids.Add(id);
            await session.ExecuteAsync(insert.Bind(id, NewHotelId(), new LocalDate(2026, 12, day), guest));
        }

        try
        {
            // Indexes created on the first run build asynchronously; retry briefly until they are queryable.
            var byGuest = await WhenIndexReady(() => session.ExecuteAsync(new SimpleStatement(
                "SELECT confirmation_number FROM lab_reservations_sai WHERE guest_id = ?", alice)));
            Require(byGuest.Count() == 3, "SAI lookup by guest_id should return 3 reservations.");
            Console.WriteLine("SAI：WHERE guest_id = ? 无需 ALLOW FILTERING，返回该客人的 3 笔预订。");

            var combined = await WhenIndexReady(() => session.ExecuteAsync(new SimpleStatement(
                "SELECT confirmation_number FROM lab_reservations_sai WHERE guest_id = ? AND start_date >= ?",
                alice, new LocalDate(2026, 12, 10))));
            Require(combined.Count() == 2, "SAI should combine equality on guest_id with a range on start_date.");
            Console.WriteLine("SAI：guest_id 等值 + start_date 范围组合查询，返回 12 月 10 日及以后的 2 笔。");
            Console.WriteLine("对比：SAI 查询要询问覆盖全部 token 范围的副本；专门建一张以 guest_id 为分区键的表只读一个分区，但要多维护一份数据。");
        }
        finally
        {
            var delete = await session.PrepareAsync("DELETE FROM lab_reservations_sai WHERE confirmation_number = ?");
            foreach (var id in ids) await session.ExecuteAsync(delete.Bind(id));
        }
    }

    // 3. Query tracing: compare a single-partition read with a filtered full scan.
    public async Task Tracing()
    {
        var hotel = NewHotelId();
        var guest = Guid.NewGuid();
        var insert = await session.PrepareAsync("""
            INSERT INTO reservations_by_hotel_date
                (hotel_id, start_date, room_number, confirmation_number, end_date, guest_id)
            VALUES (?, ?, ?, ?, ?, ?)
            """);
        for (short room = 101; room <= 120; room++)
            await session.ExecuteAsync(insert.Bind(hotel, Arrival, room, Guid.NewGuid().ToString(), new LocalDate(2026, 11, 9), guest));

        try
        {
            var byKey = await session.PrepareAsync(
                "SELECT confirmation_number FROM reservations_by_hotel_date WHERE hotel_id = ? AND start_date = ?");
            var keyTrace = await RunTraced("按完整分区键查询", byKey.Bind(hotel, Arrival));
            Require(keyTrace.Rows == 20, "Partition query should return 20 rows.");
            Require(keyTrace.Has("single-partition") || keyTrace.Has("single partition"),
                "Partition query trace should mention a single-partition read.");

            var scan = new SimpleStatement(
                "SELECT confirmation_number FROM reservations_by_hotel_date WHERE guest_id = ? ALLOW FILTERING", guest);
            var scanTrace = await RunTraced("ALLOW FILTERING 全表扫描", scan);
            Require(scanTrace.Rows == 20, "Filtered scan should return the same 20 rows.");
            Require(scanTrace.Has("range"), "Filtered scan trace should mention range requests.");

            Console.WriteLine("结果相同：前者是单分区读取，后者向所有 token 范围发起 range 请求并逐行过滤。");
            Console.WriteLine("数据量越大差距越明显；也可以在 cqlsh 中执行 TRACING ON 后运行同样的语句。");

            // A lightweight transaction: the trace shows the Paxos rounds.
            var lwt = new SimpleStatement("""
                INSERT INTO lab_reservations_by_hotel_date
                    (hotel_id, start_date, room_number, confirmation_number, end_date, guest_id)
                VALUES (?, ?, ?, ?, ?, ?) IF NOT EXISTS
                """, hotel, Arrival, (short)101, Guid.NewGuid().ToString(), new LocalDate(2026, 11, 9), guest);
            lwt.EnableTracing();
            var lwtRows = await session.ExecuteAsync(lwt);
            var lwtTrace = await lwtRows.Info.GetQueryTraceAsync();
            var paxos = lwtTrace.Events.Select(e => e.Description)
                .Where(d => d.Contains("paxos", StringComparison.OrdinalIgnoreCase) || d.Contains("propos", StringComparison.OrdinalIgnoreCase)
                    || d.Contains("promise", StringComparison.OrdinalIgnoreCase) || d.Contains("commit", StringComparison.OrdinalIgnoreCase)
                    || d.Contains("CAS", StringComparison.Ordinal))
                .Distinct().ToList();
            Console.WriteLine($"--- INSERT … IF NOT EXISTS：[applied]={lwtRows.First().GetValue<bool>("[applied]")}，耗时 {lwtTrace.DurationMicros} μs，trace 事件 {lwtTrace.Events.Count()} 个 ---");
            foreach (var description in paxos.Take(10)) Console.WriteLine($"  {description}");
            Require(paxos.Count > 0, "LWT trace should show Paxos events.");
            Console.WriteLine("条件写入要经过 Paxos 的准备、读取、提议和提交，事件明显多于普通写入。");
        }
        finally
        {
            await session.ExecuteAsync(new SimpleStatement(
                "DELETE FROM reservations_by_hotel_date WHERE hotel_id = ? AND start_date = ?", hotel, Arrival));
            await session.ExecuteAsync(new SimpleStatement(
                "DELETE FROM lab_reservations_by_hotel_date WHERE hotel_id = ? AND start_date = ?", hotel, Arrival));
        }
    }

    // 4. Tombstones: deletes and null values are writes that reads must skip over.
    public async Task Tombstones()
    {
        await session.ExecuteAsync(new SimpleStatement(
            "CREATE TABLE IF NOT EXISTS lab_tombstones (bucket text, id int, value text, PRIMARY KEY (bucket, id))"));
        var insert = await session.PrepareAsync("INSERT INTO lab_tombstones (bucket, id, value) VALUES (?, ?, ?)");
        var deleteRow = await session.PrepareAsync("DELETE FROM lab_tombstones WHERE bucket = ? AND id = ?");
        var selectBucket = await session.PrepareAsync("SELECT id, value FROM lab_tombstones WHERE bucket = ?");
        var deleteBucket = await session.PrepareAsync("DELETE FROM lab_tombstones WHERE bucket = ?");

        // Part A: 1,100 rows, 1,099 deleted. Default tombstone_warn_threshold is 1,000.
        const int total = 1100;
        var bucket = Guid.NewGuid().ToString();
        var nulls = Guid.NewGuid().ToString();
        try
        {
            await ForEachConcurrent(Enumerable.Range(0, total), i => session.ExecuteAsync(insert.Bind(bucket, i, $"v{i}")));
            await ForEachConcurrent(Enumerable.Range(1, total - 1), i => session.ExecuteAsync(deleteRow.Bind(bucket, i)));

            var trace = await RunTraced($"写入 {total} 行、逐行删除 {total - 1} 行后读取该分区", selectBucket.Bind(bucket));
            Require(trace.Rows == 1, "Only one live row should remain.");
            Require(trace.Has("tombstone"), "Trace should report tombstones scanned.");
            foreach (var warning in trace.Warnings) Console.WriteLine($"  服务端警告：{warning}");
            Require(trace.Warnings.Any(w => w.Contains("tombstone", StringComparison.OrdinalIgnoreCase)),
                "Reading past tombstone_warn_threshold should return a server warning.");
            Console.WriteLine("只返回 1 行，却要扫描上千个墓碑；超过 tombstone_failure_threshold（默认 100000）时查询会直接失败。");

            // Part B: binding null writes a cell tombstone; Unset.Value leaves the column untouched.
            await session.ExecuteAsync(insert.Bind(nulls, 1, "original"));
            await session.ExecuteAsync(insert.Bind(nulls, 1, Unset.Value));
            var afterUnset = (await session.ExecuteAsync(selectBucket.Bind(nulls))).Single().GetValue<string>("value");
            Require(afterUnset == "original", "Unset.Value must not overwrite the existing value.");
            Console.WriteLine($"绑定 Unset.Value 后 value = \"{afterUnset}\"：该列没有被写入。");

            await session.ExecuteAsync(insert.Bind(nulls, 1, null));
            var nullTrace = await RunTraced("绑定 null 后读取", selectBucket.Bind(nulls));
            Require(nullTrace.Rows == 1, "The row should still exist after writing null.");
            Require(nullTrace.Has("1 tombstone"), "Writing null should produce a cell tombstone.");
            Console.WriteLine("绑定 null 后 value 为空，而且产生了一个墓碑。可选字段请用 Unset.Value 或不写该列，不要写 null。");
        }
        finally
        {
            await session.ExecuteAsync(deleteBucket.Bind(bucket));
            await session.ExecuteAsync(deleteBucket.Bind(nulls));
        }
    }

    // 5. Consistency level vs replication factor, demonstrated on a single node.
    public async Task Consistency()
    {
        var keyspace = "rs_rf3_" + Guid.NewGuid().ToString("N");
        var dc = localDatacenter.Replace("'", "''");
        try
        {
            // Cassandra accepts RF=3 on one node (with a warning); two of the three replicas simply never exist.
            await session.ExecuteAsync(new SimpleStatement(
                $"CREATE KEYSPACE {keyspace} WITH replication = {{'class': 'NetworkTopologyStrategy', '{dc}': 3}}"));
            await session.ExecuteAsync(new SimpleStatement($"CREATE TABLE {keyspace}.kv (id int PRIMARY KEY, value text)"));
            var write = await session.PrepareAsync($"INSERT INTO {keyspace}.kv (id, value) VALUES (?, ?)");
            var read = await session.PrepareAsync($"SELECT value FROM {keyspace}.kv WHERE id = ?");

            Console.WriteLine("RF=3，但集群只有 1 个节点：");
            await ExpectUnavailable("LOCAL_QUORUM 写入", write.Bind(1, "a").SetConsistencyLevel(ConsistencyLevel.LocalQuorum));
            await session.ExecuteAsync(write.Bind(1, "a").SetConsistencyLevel(ConsistencyLevel.One));
            Console.WriteLine("  ONE 写入：成功（只需 1 个副本确认）");
            var one = await session.ExecuteAsync(read.Bind(1).SetConsistencyLevel(ConsistencyLevel.One));
            Require(one.Single().GetValue<string>("value") == "a", "ONE read should see the ONE write on the only replica.");
            Console.WriteLine("  ONE 读取：成功");
            await ExpectUnavailable("ALL 读取", read.Bind(1).SetConsistencyLevel(ConsistencyLevel.All));

            Console.WriteLine("对照：项目默认 keyspace RF=1，LOCAL_QUORUM = 1 个副本，所以 API 能正常读写。");
            Console.WriteLine("结论：一致性级别要求的是副本确认数，RF 和存活节点数决定能否满足；W + R > RF 时读才能保证看到最新写入。");
        }
        finally
        {
            await session.ExecuteAsync(new SimpleStatement($"DROP KEYSPACE IF EXISTS {keyspace}"));
        }
    }

    private async Task ExpectUnavailable(string description, IStatement statement)
    {
        try
        {
            await session.ExecuteAsync(statement);
        }
        catch (UnavailableException ex)
        {
            Console.WriteLine($"  {description}：UnavailableException，需要 {ex.RequiredReplicas} 个副本，存活 {ex.AliveReplicas} 个");
            return;
        }
        throw new InvalidOperationException($"Expected UnavailableException: {description}");
    }

    private async Task<TraceSummary> RunTraced(string title, IStatement statement)
    {
        statement.EnableTracing();
        var rows = await session.ExecuteAsync(statement);
        var count = rows.Count();
        var trace = await rows.Info.GetQueryTraceAsync();
        var events = trace.Events.Select(e => e.Description).ToList();

        Console.WriteLine($"--- {title}：返回 {count} 行，耗时 {trace.DurationMicros} μs，trace 事件 {events.Count} 个 ---");
        foreach (var description in events.Where(IsInteresting).Distinct().Take(8))
            Console.WriteLine($"  {description}");
        return new TraceSummary(count, events, rows.Info.Warnings ?? []);
    }

    private static bool IsInteresting(string description) =>
        new[] { "partition", "range", "tombstone", "live rows", "sstable", "memtable", "filter", "index" }
            .Any(k => description.Contains(k, StringComparison.OrdinalIgnoreCase));

    private static async Task<RowSet> WhenIndexReady(Func<Task<RowSet>> query)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { return await query(); }
            catch (Exception ex) when (attempt < 30 && ex is InvalidQueryException or ServerErrorException or ReadFailureException
                && ex.Message.Contains("index", StringComparison.OrdinalIgnoreCase))
            {
                await Task.Delay(1000);
            }
        }
    }

    private static async Task ForEachConcurrent(IEnumerable<int> items, Func<int, Task> action)
    {
        // Bounded concurrency: fast enough for a demo without flooding the single node.
        foreach (var chunk in items.Chunk(64)) await Task.WhenAll(chunk.Select(action));
    }

    private static string NewHotelId() => "H_LAB_" + Guid.NewGuid().ToString("N");

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed record TraceSummary(int Rows, IReadOnlyList<string> Events, IReadOnlyList<string> Warnings)
    {
        public bool Has(string text) => Events.Any(e => e.Contains(text, StringComparison.OrdinalIgnoreCase));
    }
}
