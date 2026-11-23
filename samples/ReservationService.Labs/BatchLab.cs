using Cassandra;

namespace ReservationService.Labs;

// One reservation must land in two query tables, as in CassandraReservationRepository.
// Shows: a half-finished write, a small LOGGED BATCH, and two common misuses with the
// warnings Cassandra sends back to the driver.
public sealed class BatchLab(ISession session)
{
    private static readonly LocalDate Arrival = new(2026, 11, 6);
    private static readonly LocalDate Checkout = new(2026, 11, 9);
    private const string ConfirmationColumns = "confirmation_number, hotel_id, start_date, end_date, room_number, guest_id";
    private const string HotelColumns = "hotel_id, start_date, room_number, confirmation_number, end_date, guest_id";

    public async Task Run()
    {
        var hotel = "H_DEMO_" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var confirmation = Guid.NewGuid().ToString();
        var guest = Guid.NewGuid();
        const short room = 101;
        var bulkHotels = new List<string>();

        var insertConfirmation = await session.PrepareAsync(
            $"INSERT INTO reservations_by_confirmation ({ConfirmationColumns}) VALUES (?, ?, ?, ?, ?, ?)");
        var insertHotel = await session.PrepareAsync(
            $"INSERT INTO reservations_by_hotel_date ({HotelColumns}) VALUES (?, ?, ?, ?, ?, ?)");
        var readConfirmation = await session.PrepareAsync(
            "SELECT hotel_id FROM reservations_by_confirmation WHERE confirmation_number = ?");
        var readHotel = await session.PrepareAsync(
            "SELECT confirmation_number FROM reservations_by_hotel_date WHERE hotel_id = ? AND start_date = ?");

        Console.WriteLine("一笔预订要同时写进两张查询表（和项目的 CassandraReservationRepository 一样）：");
        Console.WriteLine("  reservations_by_confirmation   按确认号查询，分区键 confirmation_number");
        Console.WriteLine("  reservations_by_hotel_date     按酒店和入住日查询，分区键 (hotel_id, start_date)");
        Console.WriteLine($"  这笔预订：确认号 {Short(confirmation)}，酒店 {hotel}，{Arrival} 入住 {room} 房");

        var confirmationValues = $"'{Short(confirmation)}', '{hotel}', '{Arrival}', '{Checkout}', {room}, {Short(guest)}";
        var hotelValues = $"'{hotel}', '{Arrival}', {room}, '{Short(confirmation)}', '{Checkout}', {Short(guest)}";
        try
        {
            // Step 1: only the first write happens.
            Console.WriteLine("\n[第 1 步] 两条独立的 INSERT，只执行了第一条（模拟应用在两次写入之间崩溃）");
            Console.WriteLine($"  CQL：INSERT INTO reservations_by_confirmation ({ConfirmationColumns})");
            Console.WriteLine($"       VALUES ({confirmationValues});");
            Console.WriteLine("  CQL：INSERT INTO reservations_by_hotel_date …   ← 没有执行");
            await session.ExecuteAsync(insertConfirmation.Bind(confirmation, hotel, Arrival, Checkout, room, guest));
            var (byConfirmation, byHotel) = await ReadBoth();
            Require(byConfirmation == 1 && byHotel == 0, "Only the first table should have the reservation.");
            Console.WriteLine("  ↑ 两张表不一致：客人按确认号查得到这笔预订，酒店前台按日期却查不到。");

            // Step 2: both writes in one small LOGGED BATCH.
            Console.WriteLine("\n[第 2 步] 用 LOGGED BATCH 把两条写入放在一起");
            Console.WriteLine("  CQL：BEGIN BATCH");
            Console.WriteLine($"         INSERT INTO reservations_by_confirmation ({ConfirmationColumns})");
            Console.WriteLine($"           VALUES ({confirmationValues});");
            Console.WriteLine($"         INSERT INTO reservations_by_hotel_date ({HotelColumns})");
            Console.WriteLine($"           VALUES ({hotelValues});");
            Console.WriteLine("       APPLY BATCH;");
            var batch = new BatchStatement().SetBatchType(BatchType.Logged)
                .Add(insertConfirmation.Bind(confirmation, hotel, Arrival, Checkout, room, guest))
                .Add(insertHotel.Bind(hotel, Arrival, room, confirmation, Checkout, guest));
            var batchResult = await session.ExecuteAsync(batch);
            PrintWarnings(batchResult, expectNone: true);
            (byConfirmation, byHotel) = await ReadBoth();
            Require(byConfirmation == 1 && byHotel == 1, "Both tables should have the reservation after the batch.");
            Console.WriteLine("  ↑ 两张表一致。LOGGED BATCH 先把整个批次写进 batchlog，协调节点中途故障时由其他节点重放，");
            Console.WriteLine("    保证两条最终都生效；但不提供隔离，其他客户端可能在执行期间读到只写了一张表的状态。");

            // Step 3: misuse — an UNLOGGED batch across many partitions to "speed up" a bulk load.
            const int partitions = 12;
            Console.WriteLine($"\n[第 3 步] 误用：用 UNLOGGED BATCH 一次写 {partitions} 个不同分区，想给批量导入提速");
            Console.WriteLine("  CQL：BEGIN UNLOGGED BATCH");
            var unlogged = new BatchStatement().SetBatchType(BatchType.Unlogged);
            for (var i = 1; i <= partitions; i++)
            {
                var bulkHotel = $"{hotel}_BULK_{i:00}";
                bulkHotels.Add(bulkHotel);
                unlogged.Add(insertHotel.Bind(bulkHotel, Arrival, room, Guid.NewGuid().ToString(), Checkout, Guid.NewGuid()));
                if (i <= 2 || i == partitions)
                    Console.WriteLine($"         INSERT INTO reservations_by_hotel_date … VALUES ('{bulkHotel}', '{Arrival}', …);");
                else if (i == 3)
                    Console.WriteLine("         …（共 12 条，hotel_id 各不相同，也就是 12 个分区）");
            }
            Console.WriteLine("       APPLY BATCH;");
            var unloggedResult = await session.ExecuteAsync(unlogged);
            var unloggedWarnings = PrintWarnings(unloggedResult);
            Require(unloggedWarnings.Any(w => w.Contains("partitions", StringComparison.OrdinalIgnoreCase)),
                "An unlogged batch across many partitions should produce a server warning.");
            Console.WriteLine("  ↑ 执行成功，但服务端发出警告：所有写入都压在一个协调节点上，由它转发给各自的副本。");

            // Step 4: misuse — one batch carrying too much data.
            const int rows = 80;
            Console.WriteLine($"\n[第 4 步] 误用：一个 LOGGED BATCH 塞进 {rows} 条写入，超过 batch_size_warn_threshold（5 KiB）");
            Console.WriteLine($"  CQL：BEGIN BATCH  INSERT …（{rows} 条）  APPLY BATCH;");
            var large = new BatchStatement().SetBatchType(BatchType.Logged);
            for (var i = 1; i <= rows; i++)
            {
                var bulkHotel = $"{hotel}_BIG_{i:00}";
                bulkHotels.Add(bulkHotel);
                large.Add(insertHotel.Bind(bulkHotel, Arrival, room, Guid.NewGuid().ToString(), Checkout, Guid.NewGuid()));
            }
            var largeResult = await session.ExecuteAsync(large);
            var largeWarnings = PrintWarnings(largeResult);
            Require(largeWarnings.Any(w => w.Contains("threshold", StringComparison.OrdinalIgnoreCase)),
                "A batch above the size threshold should produce a server warning.");
            Console.WriteLine("  ↑ 超过 5 KiB 警告，超过 50 KiB（batch_size_fail_threshold）会被直接拒绝。");

            Console.WriteLine("\n结论：BATCH 用来让少量相关写入一起生效（如同一笔预订的两张表），不是事务，也不是批量导入的捷径。");
            Console.WriteLine("批量导入应该用有并发上限的单独写入（墓碑实验写 1100 行就是每 64 条一组并发执行）。");
        }
        finally
        {
            await session.ExecuteAsync(new SimpleStatement(
                "DELETE FROM reservations_by_confirmation WHERE confirmation_number = ?", confirmation));
            foreach (var h in bulkHotels.Append(hotel))
                await session.ExecuteAsync(new SimpleStatement(
                    "DELETE FROM reservations_by_hotel_date WHERE hotel_id = ? AND start_date = ?", h, Arrival));
        }

        async Task<(int ByConfirmation, int ByHotel)> ReadBoth()
        {
            var a = (await session.ExecuteAsync(readConfirmation.Bind(confirmation))).Count();
            var b = (await session.ExecuteAsync(readHotel.Bind(hotel, Arrival))).Count();
            Console.WriteLine($"  查询：SELECT … FROM reservations_by_confirmation WHERE confirmation_number = '{Short(confirmation)}';  → {a} 行");
            Console.WriteLine($"  查询：SELECT … FROM reservations_by_hotel_date WHERE hotel_id = '{hotel}' AND start_date = '{Arrival}';  → {b} 行");
            return (a, b);
        }
    }

    private static IReadOnlyList<string> PrintWarnings(RowSet result, bool expectNone = false)
    {
        var warnings = result.Info.Warnings ?? [];
        if (warnings.Length == 0)
        {
            Console.WriteLine("  结果：执行成功，没有服务端警告");
            Require(expectNone || warnings.Length > 0, "Expected a server warning.");
        }
        else
        {
            Console.WriteLine("  结果：执行成功，但驱动收到服务端警告（RowSet.Info.Warnings）：");
            foreach (var warning in warnings) Console.WriteLine($"        {Trim(warning)}");
        }
        return warnings;
    }

    // Warnings can list every table and key; keep them to one readable line on a projector.
    private static string Trim(string text) => text.Length <= 240 ? text : text[..240] + "…";

    private static string Short(object id) => id.ToString()![..8] + "…";

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
