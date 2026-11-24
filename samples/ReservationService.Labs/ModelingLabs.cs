using Cassandra;

namespace ReservationService.Labs;

// Labs for the data-model part of the talk: where a partition lives, what a primary key
// really guarantees, how timestamps decide conflicts, and two special column kinds.
public sealed class ModelingLabs(ISession session, LiveQuery live)
{
    // Print a cqlsh command once, then stop after each write so the presenter can rerun it
    // in another terminal and show the live data really changed in the database.
    private void PrintExternalQuery(string cql) =>
        live.Print(cql, "复制下面这条命令执行；之后每次暂停时，在那个终端按 ↑ 再按 Enter 重新查询");

    private void PauseForExternalQuery() => live.Pause();

    private static readonly LocalDate Day1 = new(2026, 11, 6);
    private static readonly LocalDate Day2 = new(2026, 11, 7);
    private static readonly LocalDate Checkout = new(2026, 11, 9);

    // Partition key -> token -> position on the ring.
    public async Task Token()
    {
        var hotel = NewHotelId();
        var insert = await session.PrepareAsync("""
            INSERT INTO reservations_by_hotel_date
                (hotel_id, start_date, room_number, confirmation_number, end_date, guest_id)
            VALUES (?, ?, ?, ?, ?, ?)
            """);
        await session.ExecuteAsync(insert.Bind(hotel, Day1, (short)101, Guid.NewGuid().ToString(), Checkout, Guid.NewGuid()));
        await session.ExecuteAsync(insert.Bind(hotel, Day1, (short)102, Guid.NewGuid().ToString(), Checkout, Guid.NewGuid()));
        await session.ExecuteAsync(insert.Bind(hotel, Day2, (short)101, Guid.NewGuid().ToString(), Checkout, Guid.NewGuid()));
        try
        {
            var rows = (await session.ExecuteAsync(new SimpleStatement("""
                SELECT token(hotel_id, start_date) AS t, start_date, room_number
                FROM reservations_by_hotel_date WHERE hotel_id = ? AND start_date IN (?, ?)
                """, hotel, Day1, Day2))).ToList();
            foreach (var row in rows)
                Console.WriteLine($"  token = {row.GetValue<long>("t"),21}   start_date = {row.GetValue<LocalDate>("start_date")}   room = {row.GetValue<short>("room_number")}");

            var day1Tokens = rows.Where(r => r.GetValue<LocalDate>("start_date").Equals(Day1)).Select(r => r.GetValue<long>("t")).Distinct().ToList();
            var day2Token = rows.Single(r => r.GetValue<LocalDate>("start_date").Equals(Day2)).GetValue<long>("t");
            Require(rows.Count == 3 && day1Tokens.Count == 1 && day1Tokens[0] != day2Token,
                "Rows in one partition must share a token; another start_date must hash elsewhere.");
            Console.WriteLine("同一分区键 (hotel_id, start_date) 的两行 token 相同；换一个日期就落到环上另一个位置。");

            var local = (await session.ExecuteAsync(new SimpleStatement("SELECT tokens, partitioner FROM system.local"))).Single();
            var vnodes = local.GetValue<IEnumerable<string>>("tokens").Count();
            Console.WriteLine($"本节点在环上占 {vnodes} 个位置（虚拟节点），分区器：{local.GetValue<string>("partitioner")}");
            Require(vnodes > 1, "A node with vnodes should own more than one token.");
            Console.WriteLine("在容器里查看某个分区落在哪些节点：nodetool getendpoints reservation_labs reservations_by_hotel_date '<hotel_id>:<start_date>'");
        }
        finally
        {
            await session.ExecuteAsync(new SimpleStatement(
                "DELETE FROM reservations_by_hotel_date WHERE hotel_id = ? AND start_date IN (?, ?)", hotel, Day1, Day2));
        }
    }

    // INSERT is an upsert and UPDATE creates rows: the primary key alone decides identity.
    public async Task Overwrite()
    {
        var hotel = NewHotelId();
        try
        {
            // lab_reservations_by_hotel_date keeps the original key ((hotel_id, start_date), room_number).
            var narrow = await session.PrepareAsync("""
                INSERT INTO lab_reservations_by_hotel_date
                    (hotel_id, start_date, room_number, confirmation_number, end_date, guest_id)
                VALUES (?, ?, ?, ?, ?, ?)
                """);
            // Short, readable confirmation numbers: the hotel id is unique per run, so they cannot collide.
            const string first = "R-001";
            const string second = "R-002";

            Console.WriteLine("① 主键 ((hotel_id, start_date), room_number)：同一天同一房间写入两笔不同的预订\n");
            PrintExternalQuery(HotelDayQuery("lab_reservations_by_hotel_date"));
            await Write(narrow, 1, first);
            var narrowAfterFirst = await Show("lab_reservations_by_hotel_date");
            Require(narrowAfterFirst.SequenceEqual([first]), "The first write must create one row.");
            PauseForExternalQuery();
            await Write(narrow, 2, second);
            var narrowAfterSecond = await Show("lab_reservations_by_hotel_date");
            Require(narrowAfterSecond.SequenceEqual([second]), "Same primary key must silently overwrite.");
            Console.WriteLine($"          ↑ 第二笔没有报错，表里仍然只有 1 行：{first} 被 {second} 静默覆盖\n");
            PauseForExternalQuery();

            var wide = await session.PrepareAsync("""
                INSERT INTO reservations_by_hotel_date
                    (hotel_id, start_date, room_number, confirmation_number, end_date, guest_id)
                VALUES (?, ?, ?, ?, ?, ?)
                """);
            Console.WriteLine("② 主键加入 confirmation_number：((hotel_id, start_date), room_number, confirmation_number)，同样写两笔\n");
            PrintExternalQuery(HotelDayQuery("reservations_by_hotel_date"));
            await Write(wide, 1, first);
            var wideAfterFirst = await Show("reservations_by_hotel_date");
            Require(wideAfterFirst.SequenceEqual([first]), "The first write must create one row.");
            PauseForExternalQuery();
            await Write(wide, 2, second);
            var wideAfterSecond = await Show("reservations_by_hotel_date");
            Require(wideAfterSecond.SequenceEqual([first, second]), "Adding confirmation_number to the key must keep both reservations.");
            Console.WriteLine("          ↑ 主键不同，两笔预订都保留。项目的酒店日期表就是这样修正的\n");
            PauseForExternalQuery();

            var ghost = "R-404";
            Console.WriteLine($"③ UPDATE 一个不存在的确认号 {ghost}\n");
            PrintExternalQuery($"SELECT confirmation_number, hotel_id, start_date FROM reservation_labs.reservations_by_confirmation WHERE confirmation_number = '{ghost}';");
            Console.WriteLine($"第 1 步  UPDATE reservations_by_confirmation SET hotel_id = … WHERE confirmation_number = '{ghost}'");
            await session.ExecuteAsync(new SimpleStatement(
                "UPDATE reservations_by_confirmation SET hotel_id = ? WHERE confirmation_number = ?", hotel, ghost));
            Console.WriteLine("        执行成功，没有报“影响 0 行”");
            var created = (await session.ExecuteAsync(new SimpleStatement(
                "SELECT hotel_id, start_date FROM reservations_by_confirmation WHERE confirmation_number = ?", ghost))).SingleOrDefault();
            Require(created is not null, "UPDATE on a missing key must create the row.");
            var startDate = created!.IsNull("start_date") ? "null" : created.GetValue<LocalDate>("start_date").ToString();
            Console.WriteLine($"        查询：1 行，hotel_id = {created.GetValue<string>("hotel_id")}，start_date = {startDate}");
            Console.WriteLine("          ↑ UPDATE 创建了一行，其余列为空\n");
            PauseForExternalQuery();
            Console.WriteLine("结论：INSERT 和 UPDATE 都是 upsert，主键决定是不是同一行。");
            Console.WriteLine("需要“不存在才插入”或“存在才更新”时，用 IF NOT EXISTS / IF EXISTS（lwt 实验）。");
            await session.ExecuteAsync(new SimpleStatement("DELETE FROM reservations_by_confirmation WHERE confirmation_number = ?", ghost));
        }
        finally
        {
            await session.ExecuteAsync(new SimpleStatement(
                "DELETE FROM lab_reservations_by_hotel_date WHERE hotel_id = ? AND start_date = ?", hotel, Day1));
            await session.ExecuteAsync(new SimpleStatement(
                "DELETE FROM reservations_by_hotel_date WHERE hotel_id = ? AND start_date = ?", hotel, Day1));
        }

        async Task Write(PreparedStatement insert, int step, string confirmation)
        {
            Console.WriteLine($"第 {step} 步  INSERT 房间 101，{Day1}，confirmation_number = '{confirmation}'");
            await session.ExecuteAsync(insert.Bind(hotel, Day1, (short)101, confirmation, Checkout, Guid.NewGuid()));
            Console.WriteLine("        写入成功，没有报错");
        }

        async Task<List<string>> Show(string table)
        {
            var confirmations = (await session.ExecuteAsync(new SimpleStatement(
                    $"SELECT confirmation_number FROM {table} WHERE hotel_id = ? AND start_date = ?", hotel, Day1)))
                .Select(row => row.GetValue<string>("confirmation_number"))
                .Order(StringComparer.Ordinal)
                .ToList();
            Console.WriteLine($"        查询：{confirmations.Count} 行 → {string.Join("、", confirmations)}");
            return confirmations;
        }

        string HotelDayQuery(string table) =>
            $"SELECT hotel_id, start_date, room_number, confirmation_number FROM reservation_labs.{table} WHERE hotel_id = '{hotel}' AND start_date = '{Day1}';";
    }

    // Last write wins by timestamp, not by arrival order.
    public async Task LastWriteWins()
    {
        var bucket = Guid.NewGuid().ToString();
        var write = await session.PrepareAsync("INSERT INTO lab_items (bucket, id, value) VALUES (?, ?, ?) USING TIMESTAMP ?");
        var read = await session.PrepareAsync("SELECT value, WRITETIME(value) AS ts FROM lab_items WHERE bucket = ? AND id = ?");
        try
        {
            // Timestamps are microseconds since the epoch, as Cassandra stores them.
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;

            Console.WriteLine("三个节点先后写同一行同一列（真实到达顺序：A → B → C）：\n");
            PrintExternalQuery($"SELECT value, WRITETIME(value) FROM reservation_labs.lab_items WHERE bucket = '{bucket}' AND id = 1;");
            await Step(1, "节点 A（时钟正常）", now, "A 写入");
            var afterA = await Show("A 写入");
            PauseForExternalQuery();

            // Arrives after A in real time, but B's clock is 5 seconds slow.
            await Step(2, "节点 B（时钟慢 5 秒）", now - 5_000_000, "B 写入");
            var afterB = await Show("A 写入");
            Require(afterB.Value == "A 写入" && afterB.WriteTime == afterA.WriteTime,
                "A later write with a smaller timestamp must be discarded.");
            Console.WriteLine("          ↑ B 明明后写，而且没有报错，但它的时间戳更小，被静默丢弃\n");
            PauseForExternalQuery();

            // Arrives last with a normal clock: larger timestamp, so it wins.
            await Step(3, "节点 C（时钟正常，晚 1 秒）", now + 1_000_000, "C 写入");
            var afterC = await Show("C 写入");
            Require(afterC.Value == "C 写入", "A write with a larger timestamp must overwrite.");
            Console.WriteLine("          ↑ C 的时间戳最大，覆盖成功\n");
            PauseForExternalQuery();

            Console.WriteLine($"第 4 步  DELETE … USING TIMESTAMP {Format(now)}（比 C 早 1 秒）");
            await session.ExecuteAsync(new SimpleStatement(
                "DELETE FROM lab_items USING TIMESTAMP ? WHERE bucket = ? AND id = ?", now, bucket, 1));
            Console.WriteLine("        执行成功，没有报错");
            var afterDelete = await Show("C 写入");
            Require(afterDelete.Value == "C 写入", "An older delete must not remove newer data.");
            Console.WriteLine("          ↑ 删除也按时间戳比较，时间戳更早的 DELETE 删不掉更新的数据\n");
            PauseForExternalQuery();

            Console.WriteLine("结论：冲突按时间戳决定，不按到达顺序。所有节点和应用服务器都要同步时钟（NTP）。");
        }
        finally
        {
            await session.ExecuteAsync(new SimpleStatement("DELETE FROM lab_items WHERE bucket = ?", bucket));
        }

        async Task Step(int number, string writer, long timestamp, string value)
        {
            Console.WriteLine($"第 {number} 步  {writer,-14} INSERT value = '{value}' USING TIMESTAMP {Format(timestamp)}");
            await session.ExecuteAsync(write.Bind(bucket, 1, value, timestamp));
            Console.WriteLine("        写入成功，没有报错");
        }

        async Task<(string Value, long WriteTime)> Show(string expected)
        {
            var row = (await session.ExecuteAsync(read.Bind(bucket, 1))).Single();
            var value = row.GetValue<string>("value");
            var writeTime = row.GetValue<long>("ts");
            var mark = value == expected ? "" : "  （与预期不符）";
            Console.WriteLine($"        读取：value = '{value}'，WRITETIME = {Format(writeTime)}{mark}");
            return (value, writeTime);
        }

        static string Format(long micros) =>
            DateTimeOffset.FromUnixTimeMilliseconds(micros / 1000).ToLocalTime().ToString("HH:mm:ss.fff");
    }

    // Static columns are stored once per partition; counters are a separate kind of table.
    public async Task StaticAndCounter()
    {
        await session.ExecuteAsync(new SimpleStatement("""
            CREATE TABLE IF NOT EXISTS lab_rooms_by_hotel (
                hotel_id    text,
                hotel_name  text STATIC,
                room_number smallint,
                room_type   text,
                PRIMARY KEY (hotel_id, room_number)
            )
            """));
        await session.ExecuteAsync(new SimpleStatement("""
            CREATE TABLE IF NOT EXISTS lab_reservation_counts (
                hotel_id   text,
                start_date date,
                total      counter,
                PRIMARY KEY (hotel_id, start_date)
            )
            """));
        var hotel = NewHotelId();
        try
        {
            var room = await session.PrepareAsync("INSERT INTO lab_rooms_by_hotel (hotel_id, room_number, room_type) VALUES (?, ?, ?)");
            foreach (var (number, type) in new (short, string)[] { (101, "大床房"), (102, "双床房"), (201, "套房") })
                await session.ExecuteAsync(room.Bind(hotel, number, type));
            await session.ExecuteAsync(new SimpleStatement(
                "UPDATE lab_rooms_by_hotel SET hotel_name = ? WHERE hotel_id = ?", "北京饭店", hotel));
            var rooms = (await session.ExecuteAsync(new SimpleStatement(
                "SELECT room_number, room_type, hotel_name FROM lab_rooms_by_hotel WHERE hotel_id = ?", hotel))).ToList();
            foreach (var r in rooms)
                Console.WriteLine($"  {r.GetValue<short>("room_number")}  {r.GetValue<string>("room_type"),-4}  hotel_name = {r.GetValue<string>("hotel_name")}");
            Require(rooms.Count == 3 && rooms.All(r => r.GetValue<string>("hotel_name") == "北京饭店"),
                "A static column written once must be visible on every row of the partition.");
            Console.WriteLine("静态列只写了一次（只给分区键），分区内 3 行都能读到：酒店信息不用每个房间存一份。");

            var increment = await session.PrepareAsync(
                "UPDATE lab_reservation_counts SET total = total + 1 WHERE hotel_id = ? AND start_date = ?");
            for (var i = 0; i < 3; i++) await session.ExecuteAsync(increment.Bind(hotel, Day1));
            var total = (await session.ExecuteAsync(new SimpleStatement(
                "SELECT total FROM lab_reservation_counts WHERE hotel_id = ? AND start_date = ?", hotel, Day1))).Single().GetValue<long>("total");
            Require(total == 3, "Three increments must give 3.");
            Console.WriteLine($"计数器：执行 3 次 total = total + 1，结果 {total}。");

            try
            {
                await session.ExecuteAsync(new SimpleStatement(
                    "INSERT INTO lab_reservation_counts (hotel_id, start_date, total) VALUES (?, ?, 10)", hotel, Day2));
                throw new InvalidOperationException("INSERT into a counter table must be rejected.");
            }
            catch (InvalidQueryException ex)
            {
                Console.WriteLine($"计数器表不能 INSERT：{ex.Message}");
            }
            Console.WriteLine("计数器不是幂等的：超时后重试可能重复计数，适合浏览量这类允许误差的统计，不适合金额和库存。");
        }
        finally
        {
            await session.ExecuteAsync(new SimpleStatement("DELETE FROM lab_rooms_by_hotel WHERE hotel_id = ?", hotel));
            await session.ExecuteAsync(new SimpleStatement("DELETE FROM lab_reservation_counts WHERE hotel_id = ?", hotel));
        }
    }

    private static string NewHotelId() => "H_LAB_" + Guid.NewGuid().ToString("N");

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
