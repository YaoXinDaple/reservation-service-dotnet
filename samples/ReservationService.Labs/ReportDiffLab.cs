using Cassandra;

namespace ReservationService.Labs;

// A scaled-down version of the report comparison model in the talk:
// two sources (legacy ledger / Lemon Cloud) per monthly period, versioned overwrites,
// diffs computed once after writes, and LWT guarding version switches.
public sealed class ReportDiffLab
{
    private const string ReportType = "BALANCE";
    private const string Legacy = "LEGACY";
    private const string Lemon = "LEMON";

    private static readonly (string Code, string Name)[] Subjects =
    [
        ("1001", "库存现金"), ("1002", "银行存款"), ("1012", "其他货币资金"), ("1122", "应收账款"), ("1123", "预付账款")
    ];

    private readonly ISession _session;
    private PreparedStatement _ensurePeriod = null!;
    private PreparedStatement _insertRow = null!;
    private PreparedStatement _readPeriod = null!;
    private PreparedStatement _readDirectory = null!;
    private PreparedStatement _deleteVersion = null!;
    private PreparedStatement _deleteDiffs = null!;
    private PreparedStatement _insertMismatch = null!;
    private PreparedStatement _insertOnlyLegacy = null!;
    private PreparedStatement _insertOnlyLemon = null!;
    private PreparedStatement _recordCompare = null!;
    private readonly Dictionary<string, PreparedStatement> _markWriting = new();
    private readonly Dictionary<string, PreparedStatement> _switchVersion = new();

    public ReportDiffLab(ISession session) => _session = session;

    public async Task Run()
    {
        await EnsureSchemaAsync();
        var importId = "IMP_" + Guid.NewGuid().ToString("N")[..12];
        var months = new[] { new LocalDate(2024, 1, 1), new LocalDate(2024, 2, 1), new LocalDate(2024, 3, 1) };
        var february = months[1];
        try
        {
            Console.WriteLine($"import_id = {importId}，报表类型 {ReportType}，3 个月期间 × 5 个科目");
            foreach (var month in months)
                await WriteSourceAsync(importId, month, Legacy, LegacyRows(month));
            foreach (var month in months)
                await WriteSourceAsync(importId, month, Lemon, month.Equals(february) ? LemonRowsWithDifferences(month) : LemonRows(month));

            Console.WriteLine("\n[Q1 总览] 按 import_id 查目录表：");
            var overview = await PrintOverviewAsync(importId);
            Require(overview[february] == 3 && overview.Where(p => !p.Key.Equals(february)).All(p => p.Value == 0),
                "February must have 3 differences and other months none.");

            Console.WriteLine("\n[Q3 差异] 按 import_id + 报表类型 + 时间区间查差异表：");
            var diffs = await PrintDiffsAsync(importId, months[0], months[^1]);
            Require(diffs.SetEquals(["1012:VALUE_MISMATCH", "1123:ONLY_LEGACY", "2241:ONLY_LEMON"]),
                "Expected one value mismatch, one legacy-only row and one Lemon-only row.");
            Console.WriteLine("柠檬云的金额精度是 4 位小数、旧账端是 2 位（如 1000.00 与 1000.0000），按数值比较时判为一致，没有误报。");

            Console.WriteLine("\n柠檬云重新导入 2 月（新版本数据与旧账端一致）……");
            await WriteSourceAsync(importId, february, Lemon, LemonRows(february));
            overview = await PrintOverviewAsync(importId);
            Require(overview.Values.All(count => count == 0), "After the corrected overwrite there must be no differences.");
            Require((await PrintDiffsAsync(importId, months[0], months[^1], quiet: true)).Count == 0, "Diff table must be empty.");

            var februaryRows = (await _session.ExecuteAsync(_readPeriod.Bind(importId, ReportType, february))).ToList();
            Require(februaryRows.Count(r => r.GetValue<string>("source") == Lemon) == Subjects.Length,
                "The old Lemon version must be deleted after the switch.");
            Console.WriteLine("2 月柠檬云旧版本已按 (期间, 来源, 版本) 前缀整体删除，只留下新版本的 5 行。");

            Console.WriteLine("\n[Q2 明细] 一次范围查询读取 3 个月、两个来源的数据：");
            var range = (await _session.ExecuteAsync(new SimpleStatement("""
                SELECT period_month, source FROM lab_report_rows_by_import
                WHERE import_id = ? AND report_type = ? AND period_month >= ? AND period_month <= ?
                """, importId, ReportType, months[0], months[^1]))).ToList();
            Console.WriteLine($"  返回 {range.Count} 行（3 个月 × 2 个来源 × 5 个科目）");
            Require(range.Count == months.Length * 2 * Subjects.Length, "Range query must return every current row.");
        }
        finally
        {
            await _session.ExecuteAsync(new SimpleStatement("DELETE FROM lab_report_rows_by_import WHERE import_id = ? AND report_type = ?", importId, ReportType));
            await _session.ExecuteAsync(new SimpleStatement("DELETE FROM lab_report_diffs_by_import WHERE import_id = ? AND report_type = ?", importId, ReportType));
            await _session.ExecuteAsync(new SimpleStatement("DELETE FROM lab_report_periods_by_import WHERE import_id = ?", importId));
        }
    }

    // Write a new version, switch to it with LWT, compare the period, then drop the old version.
    private async Task WriteSourceAsync(string importId, LocalDate month, string source, IReadOnlyList<SubjectRow> rows)
    {
        var prefix = source == Legacy ? "legacy" : "lemon";
        await _session.ExecuteAsync(_ensurePeriod.Bind(importId, ReportType, month));
        await _session.ExecuteAsync(_markWriting[source].Bind(importId, ReportType, month));

        var version = TimeUuid.NewId();
        // Same partition, small rows: bounded concurrent single writes, no large batch.
        foreach (var chunk in rows.Chunk(64))
            await Task.WhenAll(chunk.Select(row => _session.ExecuteAsync(_insertRow.Bind(
                [importId, ReportType, month, source, version, row.Code, row.Name, .. row.Amounts.Cast<object>()]))));

        var current = (await _session.ExecuteAsync(_readDirectory.Bind(importId, ReportType, month))).Single();
        TimeUuid? oldVersion = current.IsNull($"{prefix}_version") ? null : current.GetValue<TimeUuid>($"{prefix}_version");
        var switched = await _session.ExecuteAsync(_switchVersion[source].Bind(version, rows.Count, importId, ReportType, month, oldVersion));
        Require(switched.First().GetValue<bool>("[applied]"), "Version switch must apply when no other writer interfered.");

        await CompareAsync(importId, month);
        if (oldVersion is { } old)
            await _session.ExecuteAsync(_deleteVersion.Bind(importId, ReportType, month, source, old));
    }

    private async Task CompareAsync(string importId, LocalDate month)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var directory = (await _session.ExecuteAsync(_readDirectory.Bind(importId, ReportType, month))).Single();
            if (directory.GetValue<string>("legacy_status") != "READY" || directory.GetValue<string>("lemon_status") != "READY")
                return;   // the other side has not arrived yet
            var legacyVersion = directory.GetValue<TimeUuid>("legacy_version");
            var lemonVersion = directory.GetValue<TimeUuid>("lemon_version");

            // One slice of one partition; each source comes back sorted by subject_code.
            var rows = (await _session.ExecuteAsync(_readPeriod.Bind(importId, ReportType, month))).ToList();
            var legacy = rows.Where(r => r.GetValue<string>("source") == Legacy && r.GetValue<TimeUuid>("version") == legacyVersion).Select(ToSubjectRow).ToList();
            var lemon = rows.Where(r => r.GetValue<string>("source") == Lemon && r.GetValue<TimeUuid>("version") == lemonVersion).Select(ToSubjectRow).ToList();
            var diffs = Merge(legacy, lemon);

            await _session.ExecuteAsync(_deleteDiffs.Bind(importId, ReportType, month));
            foreach (var diff in diffs)
            {
                // The missing side is simply not written: no null, so no tombstone.
                var statement = diff.Type switch
                {
                    "ONLY_LEGACY" => _insertOnlyLegacy.Bind(importId, ReportType, month, diff.Code, diff.Name, diff.Type, ToAmounts(diff.Legacy!)),
                    "ONLY_LEMON" => _insertOnlyLemon.Bind(importId, ReportType, month, diff.Code, diff.Name, diff.Type, ToAmounts(diff.Lemon!)),
                    _ => _insertMismatch.Bind(importId, ReportType, month, diff.Code, diff.Name, diff.Type,
                        diff.Fields, ToAmounts(diff.Legacy!), ToAmounts(diff.Lemon!))
                };
                await _session.ExecuteAsync(statement);
            }

            var recorded = await _session.ExecuteAsync(_recordCompare.Bind(
                diffs.Count, legacyVersion, lemonVersion, DateTimeOffset.UtcNow, importId, ReportType, month, legacyVersion, lemonVersion));
            if (recorded.First().GetValue<bool>("[applied]")) return;
            // A source switched version while we compared: compare again with the new versions.
        }
        throw new InvalidOperationException("Comparison kept racing with concurrent overwrites.");
    }

    // Two-pointer merge over rows already sorted by subject_code.
    private static List<Diff> Merge(List<SubjectRow> legacy, List<SubjectRow> lemon)
    {
        var diffs = new List<Diff>();
        int i = 0, j = 0;
        while (i < legacy.Count || j < lemon.Count)
        {
            var order = i == legacy.Count ? 1 : j == lemon.Count ? -1 : string.CompareOrdinal(legacy[i].Code, lemon[j].Code);
            if (order < 0) { diffs.Add(new Diff(legacy[i].Code, legacy[i].Name, "ONLY_LEGACY", [], legacy[i].Amounts, null)); i++; }
            else if (order > 0) { diffs.Add(new Diff(lemon[j].Code, lemon[j].Name, "ONLY_LEMON", [], null, lemon[j].Amounts)); j++; }
            else
            {
                // decimal == compares numerically: 1000.00 == 1000.0000.
                var fields = Enumerable.Range(0, 8).Where(k => legacy[i].Amounts[k] != lemon[j].Amounts[k]).Select(k => $"a{k + 1}").ToList();
                if (fields.Count > 0)
                    diffs.Add(new Diff(legacy[i].Code, legacy[i].Name, "VALUE_MISMATCH", fields, legacy[i].Amounts, lemon[j].Amounts));
                i++; j++;
            }
        }
        return diffs;
    }

    private async Task<Dictionary<LocalDate, int>> PrintOverviewAsync(string importId)
    {
        var rows = await _session.ExecuteAsync(new SimpleStatement("""
            SELECT period_month, legacy_row_count, lemon_row_count, diff_count
            FROM lab_report_periods_by_import WHERE import_id = ?
            """, importId));
        var result = new Dictionary<LocalDate, int>();
        foreach (var row in rows)
        {
            var month = row.GetValue<LocalDate>("period_month");
            var count = row.GetValue<int>("diff_count");
            result[month] = count;
            Console.WriteLine($"  {month}  旧账端 {row.GetValue<int>("legacy_row_count")} 行  柠檬云 {row.GetValue<int>("lemon_row_count")} 行  差异 {count}");
        }
        return result;
    }

    private async Task<HashSet<string>> PrintDiffsAsync(string importId, LocalDate from, LocalDate to, bool quiet = false)
    {
        var rows = await _session.ExecuteAsync(new SimpleStatement("""
            SELECT period_month, subject_code, subject_name, diff_type, diff_fields, legacy, lemon
            FROM lab_report_diffs_by_import
            WHERE import_id = ? AND report_type = ? AND period_month >= ? AND period_month <= ?
            """, importId, ReportType, from, to));
        var result = new HashSet<string>();
        foreach (var row in rows)
        {
            var code = row.GetValue<string>("subject_code");
            var type = row.GetValue<string>("diff_type");
            result.Add($"{code}:{type}");
            if (quiet) continue;
            var fields = row.IsNull("diff_fields") ? "" : string.Join(",", row.GetValue<IEnumerable<string>>("diff_fields"));
            var legacy = row.IsNull("legacy") ? "—" : row.GetValue<Amounts>("legacy").A3.ToString();
            var lemon = row.IsNull("lemon") ? "—" : row.GetValue<Amounts>("lemon").A3.ToString();
            Console.WriteLine($"  {row.GetValue<LocalDate>("period_month")}  {code} {row.GetValue<string>("subject_name"),-6}  {type,-14}  字段 [{fields}]  a3：旧账端 {legacy} / 柠檬云 {lemon}");
        }
        return result;
    }

    private static List<SubjectRow> LegacyRows(LocalDate month) =>
        Subjects.Select((s, index) => new SubjectRow(s.Code, s.Name, BaseAmounts(month, index))).ToList();

    // Same numbers, stored with 4 decimal places instead of 2.
    private static List<SubjectRow> LemonRows(LocalDate month) =>
        LegacyRows(month).Select(r => r with { Amounts = r.Amounts.Select(a => a * 1.00m).ToArray() }).ToList();

    private static List<SubjectRow> LemonRowsWithDifferences(LocalDate month)
    {
        var rows = LemonRows(month);
        var mismatch = rows.Single(r => r.Code == "1012");
        mismatch.Amounts[2] += 0.01m;                              // one cent off on a3
        rows.RemoveAll(r => r.Code == "1123");                     // missing on the Lemon side
        rows.Add(new SubjectRow("2241", "其他应付款", BaseAmounts(month, 9)));   // only on the Lemon side
        return rows.OrderBy(r => r.Code, StringComparer.Ordinal).ToList();
    }

    private static decimal[] BaseAmounts(LocalDate month, int index) =>
        Enumerable.Range(1, 8).Select(k => decimal.Round(1000m * (index + 1) + month.Month * 10m + k + 0.25m * k, 2)).ToArray();

    private static SubjectRow ToSubjectRow(Row row) => new(
        row.GetValue<string>("subject_code"), row.GetValue<string>("subject_name"),
        Enumerable.Range(1, 8).Select(k => row.GetValue<decimal>($"amount_{k}")).ToArray());

    private static Amounts ToAmounts(decimal[] a) => new()
    { A1 = a[0], A2 = a[1], A3 = a[2], A4 = a[3], A5 = a[4], A6 = a[5], A7 = a[6], A8 = a[7] };

    private async Task EnsureSchemaAsync()
    {
        await _session.ExecuteAsync(new SimpleStatement("""
            CREATE TYPE IF NOT EXISTS lab_amounts (
                a1 decimal, a2 decimal, a3 decimal, a4 decimal, a5 decimal, a6 decimal, a7 decimal, a8 decimal)
            """));
        await _session.ExecuteAsync(new SimpleStatement("""
            CREATE TABLE IF NOT EXISTS lab_report_periods_by_import (
                import_id text, report_type text, period_month date,
                legacy_version timeuuid, legacy_status text, legacy_row_count int,
                lemon_version timeuuid, lemon_status text, lemon_row_count int,
                diff_count int, compared_legacy_version timeuuid, compared_lemon_version timeuuid, compared_at timestamp,
                PRIMARY KEY ((import_id), report_type, period_month))
            """));
        await _session.ExecuteAsync(new SimpleStatement("""
            CREATE TABLE IF NOT EXISTS lab_report_rows_by_import (
                import_id text, report_type text, period_month date, source text, version timeuuid,
                subject_code text, subject_name text,
                amount_1 decimal, amount_2 decimal, amount_3 decimal, amount_4 decimal,
                amount_5 decimal, amount_6 decimal, amount_7 decimal, amount_8 decimal,
                PRIMARY KEY ((import_id, report_type), period_month, source, version, subject_code))
            """));
        await _session.ExecuteAsync(new SimpleStatement("""
            CREATE TABLE IF NOT EXISTS lab_report_diffs_by_import (
                import_id text, report_type text, period_month date, subject_code text, subject_name text,
                diff_type text, diff_fields frozen<set<text>>, legacy frozen<lab_amounts>, lemon frozen<lab_amounts>,
                PRIMARY KEY ((import_id, report_type), period_month, subject_code))
            """));

        await _session.UserDefinedTypes.DefineAsync(UdtMap.For<Amounts>("lab_amounts")
            .Map(a => a.A1, "a1").Map(a => a.A2, "a2").Map(a => a.A3, "a3").Map(a => a.A4, "a4")
            .Map(a => a.A5, "a5").Map(a => a.A6, "a6").Map(a => a.A7, "a7").Map(a => a.A8, "a8"));

        _ensurePeriod = await _session.PrepareAsync(
            "INSERT INTO lab_report_periods_by_import (import_id, report_type, period_month, diff_count) VALUES (?, ?, ?, 0) IF NOT EXISTS");
        _insertRow = await _session.PrepareAsync("""
            INSERT INTO lab_report_rows_by_import (import_id, report_type, period_month, source, version, subject_code, subject_name,
                amount_1, amount_2, amount_3, amount_4, amount_5, amount_6, amount_7, amount_8)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
            """);
        _readPeriod = await _session.PrepareAsync("""
            SELECT source, version, subject_code, subject_name,
                   amount_1, amount_2, amount_3, amount_4, amount_5, amount_6, amount_7, amount_8
            FROM lab_report_rows_by_import WHERE import_id = ? AND report_type = ? AND period_month = ?
            """);
        _readDirectory = await _session.PrepareAsync("""
            SELECT legacy_version, legacy_status, lemon_version, lemon_status
            FROM lab_report_periods_by_import WHERE import_id = ? AND report_type = ? AND period_month = ?
            """);
        _deleteVersion = await _session.PrepareAsync("""
            DELETE FROM lab_report_rows_by_import
            WHERE import_id = ? AND report_type = ? AND period_month = ? AND source = ? AND version = ?
            """);
        _deleteDiffs = await _session.PrepareAsync(
            "DELETE FROM lab_report_diffs_by_import WHERE import_id = ? AND report_type = ? AND period_month = ?");
        _insertMismatch = await _session.PrepareAsync("""
            INSERT INTO lab_report_diffs_by_import
                (import_id, report_type, period_month, subject_code, subject_name, diff_type, diff_fields, legacy, lemon)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
            """);
        _insertOnlyLegacy = await _session.PrepareAsync("""
            INSERT INTO lab_report_diffs_by_import
                (import_id, report_type, period_month, subject_code, subject_name, diff_type, legacy)
            VALUES (?, ?, ?, ?, ?, ?, ?)
            """);
        _insertOnlyLemon = await _session.PrepareAsync("""
            INSERT INTO lab_report_diffs_by_import
                (import_id, report_type, period_month, subject_code, subject_name, diff_type, lemon)
            VALUES (?, ?, ?, ?, ?, ?, ?)
            """);
        _recordCompare = await _session.PrepareAsync("""
            UPDATE lab_report_periods_by_import
            SET diff_count = ?, compared_legacy_version = ?, compared_lemon_version = ?, compared_at = ?
            WHERE import_id = ? AND report_type = ? AND period_month = ?
            IF legacy_version = ? AND lemon_version = ?
            """);
        foreach (var (source, prefix) in new[] { (Legacy, "legacy"), (Lemon, "lemon") })
        {
            _markWriting[source] = await _session.PrepareAsync(
                $"UPDATE lab_report_periods_by_import SET {prefix}_status = 'WRITING' WHERE import_id = ? AND report_type = ? AND period_month = ?");
            _switchVersion[source] = await _session.PrepareAsync($"""
                UPDATE lab_report_periods_by_import
                SET {prefix}_version = ?, {prefix}_status = 'READY', {prefix}_row_count = ?
                WHERE import_id = ? AND report_type = ? AND period_month = ?
                IF {prefix}_version = ?
                """);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed record SubjectRow(string Code, string Name, decimal[] Amounts);

    private sealed record Diff(string Code, string Name, string Type, List<string> Fields, decimal[]? Legacy, decimal[]? Lemon);

    public sealed class Amounts
    {
        public decimal A1 { get; set; }
        public decimal A2 { get; set; }
        public decimal A3 { get; set; }
        public decimal A4 { get; set; }
        public decimal A5 { get; set; }
        public decimal A6 { get; set; }
        public decimal A7 { get; set; }
        public decimal A8 { get; set; }
    }
}
