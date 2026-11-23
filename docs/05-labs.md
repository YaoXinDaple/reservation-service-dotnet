# 十六项可执行实验

先启动数据库，再在项目根目录执行：

```powershell
docker compose up -d --wait --wait-timeout 600
dotnet run --project samples/ReservationService.Labs -- all
```

实验连接同一 Docker Cassandra，数据写入独立的 `reservation_labs` keyspace；schema 与 consistency 实验另外创建并清理一次性 keyspace。也可以将 `all` 换成 `prepared`、`paging`、`ttl`、`lwt`、`batch`、`schema-exists`、`anti-pattern`、`sai`、`tracing`、`tombstones`、`consistency`、`token`、`overwrite`、`lww`、`static-counter` 或 `report-diff`，一次只运行一个实验。条件写入还支持 `if-not-exists` 和 `if-exists` 两个单独入口。

每项实验会校验结果，预期行为成立时输出 PASS，否则抛出异常并以非零退出码结束。成功执行后清理该次写入的数据，TTL 数据自然过期；实验表保留。每次运行使用独立标识，重复执行互不覆盖。

## 1. Prepared Statement

```powershell
dotnet run --project samples/ReservationService.Labs -- prepared
```

准备一条 INSERT，重复绑定不同参数，其中一个文本值为 `O'Reilly`。读取后检查两行和值是否正确。

观察代码中的 `PrepareAsync`、`Bind` 和 `ExecuteAsync` 分别何时执行。尝试在值里加入中文或特殊符号，仍然通过绑定参数传递。

预期：同一个 PreparedStatement 被复用，两次写入都正确读回。

## 2. 手动分页

```powershell
dotnet run --project samples/ReservationService.Labs -- paging
```

向同一个分区写入七行，关闭自动翻页，使用 pageSize=3 遍历。程序打印每页数量与是否还有后续状态，最终检查 0 到 6 的编号是否完整且没有重复。

预期：通常看到 3、3、1 三页，最后没有 PagingState。分页状态只在控制台进程里流转，API 对外使用保护后的游标。

练习：把 pageSize 改成 2；再去掉 `SetAutoPage(false)`，比较枚举结果。理解自动分页会如何影响 HTTP 层一次请求加载的数据量。

## 3. TTL

```powershell
dotnet run --project samples/ReservationService.Labs -- ttl
```

使用 `USING TTL 5` 写入数据，立即查询 `TTL(value)`，随后每秒查询一次，最多观察 15 秒。

预期：最初能读到剩余秒数，约五秒后查询不到记录。不要把 TTL 到期等同于磁盘空间立即回收。

练习：给同一行的另一个非主键列单独设置 TTL，观察列级过期与整行可见性的关系。不要在两张预订表上随意添加不同 TTL，否则可能留下不一致的查询结果。

## 4. 轻量级事务

```powershell
dotnet run --project samples/ReservationService.Labs -- lwt
```

`lwt` 依次执行以下三个预订场景。表名为 `lab_reservations_by_hotel_date`，主键沿用原项目 LWT 分支的 `((hotel_id, start_date), room_number)`。

1. `INSERT ... IF NOT EXISTS`：第一次创建成功，同酒店、入住日、房号再次创建失败，原确认号、客人、离店日都保持不变。
2. `UPDATE/DELETE ... IF EXISTS`：更新不存在的预订不产生新行；更新已存在预订成功；第一次删除成功，再次删除返回条件不成立。
3. 八个并发条件插入：同酒店、入住日、房号恰好一个竞争者成功，并检查保存的完整预订属于该竞争者。

单独运行前两个场景：

```powershell
dotnet run --project samples/ReservationService.Labs -- if-not-exists
dotnet run --project samples/ReservationService.Labs -- if-exists
```

实际使用的条件语句：

```cql
INSERT INTO lab_reservations_by_hotel_date
    (hotel_id, start_date, room_number, confirmation_number, end_date, guest_id)
VALUES (?, ?, ?, ?, ?, ?) IF NOT EXISTS;

UPDATE lab_reservations_by_hotel_date SET end_date = ?, guest_id = ?
WHERE hotel_id = ? AND start_date = ? AND room_number = ? IF EXISTS;

DELETE FROM lab_reservations_by_hotel_date
WHERE hotel_id = ? AND start_date = ? AND room_number = ? IF EXISTS;
```

程序读取每次条件写入的 `[applied]`，再查询数据确认条件失败没有覆盖或新建记录。实验将串行一致性设为 `LOCAL_SERIAL`，普通提交阶段仍沿用本项目的 `LOCAL_QUORUM`。

预期：首次插入为 true、重复插入为 false；更新存在记录为 true、更新不存在记录为 false；首次删除为 true、再次删除为 false；八个并发插入只有一个为 true。

这一唯一性只针对相同入住日，不能防止不同入住日但住宿区间重叠的预订。主 API 仍使用允许多笔重叠预订的模型。完整说明见 [IF NOT EXISTS 与 IF EXISTS](06-conditional-writes.md)。

练习：将条件改成只有确认号匹配时才能删除，思考单纯 `IF EXISTS` 与校验当前记录归属的区别，以及条件不满足与请求超时的区别。

## 5. 冗余数据与小型 BATCH

```powershell
dotnet run --project samples/ReservationService.Labs -- batch
```

实验沿用项目的真实场景：一笔预订要同时写进 `reservations_by_confirmation` 和 `reservations_by_hotel_date` 两张表。每一步都打印实际执行的 CQL，执行后立即查询两张表或打印服务端警告：

| 步骤 | 做法 | 现场看到的结果 |
| --- | --- | --- |
| 1 | 两条独立 INSERT 只执行第一条，模拟应用在两次写入之间崩溃 | 确认号表 1 行，酒店日期表 0 行：两张表不一致 |
| 2 | 用 `BEGIN BATCH … APPLY BATCH`（LOGGED）同时写两张表 | 两张表各 1 行，没有警告 |
| 3 | 误用：`UNLOGGED BATCH` 一次写 12 个不同分区 | 执行成功，但驱动收到警告 `Unlogged batch covering 12 partitions detected … use a logged batch for atomicity, or asynchronous writes for performance` |
| 4 | 误用：一个批次塞进 80 条写入 | 执行成功，但收到警告 `Batch … is of size 11840, exceeding specified threshold of 5120` |

警告来自 `RowSet.Info.Warnings`，阈值见本地容器的 `batch_size_warn_threshold: 5KiB`、`batch_size_fail_threshold: 50KiB`、`unlogged_batch_across_partitions_warn_threshold: 10`。

预期：如上表，最后清理所有写入。

分享要点：BATCH 用来让少量相关写入一起生效，不是事务（不提供隔离），也不是批量导入的捷径。批量导入应该用有并发上限的单独写入。这个实验刻意省略第二次写入来演示应用执行不完整，没有注入真实节点故障。结合 [一致性文档](04-consistency.md) 理解实际保证。

## 6. Schema 的 IF NOT EXISTS 与 IF EXISTS

```powershell
dotnet run --project samples/ReservationService.Labs -- schema-exists
```

原项目的配置类使用 `CREATE KEYSPACE ... IF NOT EXISTS` 创建 schema，使用 `DROP KEYSPACE ... IF EXISTS` 清理 schema。这个实验在自动生成的 `rs_schema_<随机标识>` keyspace 上重复执行两次创建、两次删除，并查询 `system_schema.keyspaces` 验证结果。

预期：重复创建和重复删除都成功，最终该测试 keyspace 不存在。实验只删除本次生成的 keyspace，不清理 `reservation_service` 或 `reservation_labs`。

Schema 中的存在性判断用于重复执行 DDL。它与上一节数据条件写入中的 LWT 不同，程序不从 DDL 响应读取 `[applied]`。

## 7. 违反主键规则的查询

```powershell
dotnet run --project samples/ReservationService.Labs -- anti-pattern
```

程序先从 `system_schema.columns` 读出 `reservations_by_hotel_date` 实际的主键并打印：

```text
PRIMARY KEY ((hotel_id, start_date), room_number, confirmation_number)
分区键：hotel_id + start_date    聚簇列（按顺序）：room_number → confirmation_number
```

然后写入 3 笔预订（同一酒店：11-06 入住的 101、102 房，11-07 入住的 101 房），依次执行 6 条查询。每条查询都打印实际执行的 CQL（参数值代入显示，执行时仍然绑定参数），再打印结果：

| 查询 | 条件 | 结果 |
| --- | --- | --- |
| 1 | `WHERE hotel_id = ?`（缺少 `start_date`） | 被拒绝：分区键必须完整给出，否则需要 `ALLOW FILTERING` |
| 2 | `WHERE guest_id = ?` | 被拒绝：非主键列不能直接作为条件 |
| 3 | `WHERE hotel_id = ? AND start_date = ? AND confirmation_number = ?` | 被拒绝：不能跳过前一个聚簇列 `room_number` |
| 4 | 查询 2 加上 `ALLOW FILTERING` | 返回 1 行，但要扫描所有分区再逐行过滤 |
| 5 | `WHERE hotel_id = ? AND start_date = ?` | 返回 2 行，按聚簇列排好序 |
| 6 | 查询 3 补上 `room_number = ?` | 返回 1 行，精确定位 |

预期：前三条打印 `InvalidQueryException` 的原始错误信息，后三条分别返回 1、2、1 行。查询 3 和查询 6 只差一个 `room_number` 条件，正好对比“聚簇列不能跳过”。

分享要点：Cassandra 拒绝这些查询，是在提示开发者查询没有按分区键设计。这是“先设计查询，再设计表”的直接证据。

## 8. SAI：Cassandra 5.0 的存储附加索引

```powershell
dotnet run --project samples/ReservationService.Labs -- sai
```

在独立表 `lab_reservations_sai` 上创建两个索引：

```cql
CREATE INDEX IF NOT EXISTS lab_reservations_sai_guest ON lab_reservations_sai (guest_id) USING 'sai';
CREATE INDEX IF NOT EXISTS lab_reservations_sai_start ON lab_reservations_sai (start_date) USING 'sai';
```

写入两位客人的五笔预订，然后执行 `WHERE guest_id = ?` 和 `WHERE guest_id = ? AND start_date >= ?`，两条查询都不需要 `ALLOW FILTERING`。

预期：按客人查到 3 笔；加上日期范围后查到 2 笔。

这个实验使用单独的表，避免索引影响上一个实验对 `ALLOW FILTERING` 的演示。

对比扩展练习 1 中“按客人查询”的两种实现：

| | SAI | 反范式化的第三张表 |
| --- | --- | --- |
| 写入 | 只写一张表，索引随数据一起维护 | 每次写入多维护一张表，客人变更时要清理旧行 |
| 读取 | 协调节点要查询覆盖全部 token 范围的副本 | 只读一个分区 |
| 适合 | 低频查询、需要组合条件、选择性较好的列 | 高频、对延迟敏感的核心查询 |

## 9. 查询追踪

```powershell
dotnet run --project samples/ReservationService.Labs -- tracing
```

向同一个酒店日期分区写入 20 笔预订，分别用两种方式读回，并开启 `statement.EnableTracing()`。程序通过 `RowSet.Info.GetQueryTraceAsync()` 读取 `system_traces` 中的事件：

- 按完整分区键查询：`Executing single-partition query`、`Merged data from memtables and N sstables`、`Read 20 live rows and 0 tombstone cells`。
- `ALLOW FILTERING` 按 `guest_id` 查询：`Computing ranges to query`、`Submitting range requests on 17 ranges`、`Executing seq scan`。

最后再追踪一条 `INSERT … IF NOT EXISTS`，trace 中能看到轻量级事务的 Paxos 各阶段：`Reading existing values for CAS precondition`、`proposing client-requested updates`、`Accepting proposal`、`Committing proposal`、`CAS applied successfully`，事件数是普通查询的好几倍。

预期：两种查询都返回 20 行，trace 中分别出现 single-partition 和 range 字样；LWT 的 trace 中出现 Paxos 事件。

**观察 SSTable 和布隆过滤器**：先把数据刷到磁盘，再运行一次：

```powershell
docker compose exec cassandra nodetool flush reservation_labs
dotnet run --project samples/ReservationService.Labs -- tracing
```

按分区键查询的 trace 会多出 `Bloom filter allows skipping sstable N`：布隆过滤器判断这个分区不在某个 SSTable 里，直接跳过了它。`Merged data from memtables and N sstables` 中的 N 也会随刷盘次数变化。

单节点、小数据量时两者耗时接近，甚至全表扫描更快（第一条查询还要预热），所以分享时应关注事件内容，不要比较耗时。可以在 cqlsh 中执行 `TRACING ON` 演示同样的内容：

```powershell
docker compose exec cassandra cqlsh -k reservation_labs
```

## 10. 墓碑

```powershell
dotnet run --project samples/ReservationService.Labs -- tombstones
```

分两部分：

1. 向同一分区写入 1100 行，逐行删除 1099 行，再读取整个分区。trace 显示 `Read 1 live rows and 1099 tombstone cells`，驱动的 `RowSet.Info.Warnings` 中收到服务端警告（默认 `tombstone_warn_threshold` 为 1000）。
2. 对同一行先写入 `"original"`，再用 `Unset.Value` 绑定该列，值保持不变；改为绑定 `null` 后，该列变为空，trace 显示 `1 tombstone cells`。

预期：只返回 1 行却扫描了 1099 个墓碑；`Unset.Value` 不改动已有值；`null` 会产生墓碑。

分享要点：

- DELETE 是写入一条删除标记，读取时要跳过这些标记，直到 `gc_grace_seconds` 之后被 compaction 清除。超过 `tombstone_failure_threshold`（默认 100000）时查询会直接失败。
- 队列式用法（频繁写入再删除同一分区）是常见的墓碑问题来源。
- 用 Prepared Statement 写入可选字段时，未提供的值应绑定 `Unset.Value`，不要绑定 `null`。本项目 API 的所有列都是必填，因此不受影响。

## 11. 一致性级别与副本数

```powershell
dotnet run --project samples/ReservationService.Labs -- consistency
```

在单节点上创建 RF=3 的临时 keyspace `rs_rf3_<随机标识>`。Cassandra 允许这样建（会记录警告），但另外两个副本并不存在：

| 操作 | 结果 |
| --- | --- |
| `LOCAL_QUORUM` 写入 | `UnavailableException`：需要 2 个副本，存活 1 个 |
| `ONE` 写入 | 成功 |
| `ONE` 读取 | 成功 |
| `ALL` 读取 | `UnavailableException`：需要 3 个副本，存活 1 个 |

预期：如表所示，结束后删除该临时 keyspace。

`UnavailableException` 由协调节点在发送请求前判断，因为存活副本不足，请求根本不会执行。这与写入超时不同：超时后数据可能已经写入部分副本。

分享要点：一致性级别要求的是副本确认数，RF 和存活节点数决定能否满足。读写副本数之和大于 RF（W + R > RF）时，读取才能保证看到最新写入。项目默认 RF=1，所以 `LOCAL_QUORUM` 只需要 1 个副本。不用搭建三节点集群，也能用这个实验讲清楚 [一致性文档](04-consistency.md) 中的边界。

## 12. 分区键、token 与虚拟节点

```powershell
dotnet run --project samples/ReservationService.Labs -- token
```

写入三笔预订：两笔分区键相同 `(H, 11-06)`，一笔日期不同 `(H, 11-07)`。用 `SELECT token(hotel_id, start_date)` 读出每行的 token，再查询 `system.local` 中本节点拥有的 token 个数。

预期：同一分区键的两行 token 完全相同，另一个日期的 token 不同；本节点有 16 个 token（`num_tokens: 16`），分区器是 `Murmur3Partitioner`。

查看一个分区落在哪些节点：

```powershell
docker compose exec cassandra nodetool getendpoints reservation_labs reservations_by_hotel_date 'H_BEIJING:2026-11-06'
```

分享要点：分区键经过哈希得到 token，token 决定数据在环上的位置。分区键相同的行一定在一起，所以查询必须给出完整分区键。

## 13. 主键决定身份：静默覆盖与 UPDATE 建行

```powershell
dotnet run --project samples/ReservationService.Labs -- overwrite
```

1. 在主键为 `((hotel_id, start_date), room_number)` 的表里，同房同日写入两笔不同确认号的预订：不报错，只剩 1 行。
2. 在项目的酒店日期表里（主键多了 `confirmation_number`）做同样的写入：两行都保留。
3. 对一个不存在的确认号执行 `UPDATE`：没有“影响 0 行”，而是创建了这一行。

预期：如上，最后清理所有写入。

分享要点：CQL 的 `INSERT` 和 `UPDATE` 都是 upsert，只看主键。主键不唯一会静默丢数据；需要“不存在才插入”时用 `IF NOT EXISTS`（第 4 节）。

## 14. 最后写入者胜与时钟漂移

```powershell
dotnet run --project samples/ReservationService.Labs -- lww
```

用 `USING TIMESTAMP` 模拟三个节点按 A → B → C 的顺序写同一列，每次写完立刻读取并打印 `value` 和 `WRITETIME(value)`：

| 步骤 | 写入方 | 使用的时间戳 | 写完后读到的值 |
| --- | --- | --- | --- |
| 1 | 节点 A，时钟正常 | 当前时间 | `A 写入` |
| 2 | 节点 B，时钟慢 5 秒 | 当前时间 − 5 秒 | 仍是 `A 写入`：B 后写，但被静默丢弃 |
| 3 | 节点 C，时钟正常，晚 1 秒写 | 当前时间 + 1 秒 | `C 写入`：时间戳最大，覆盖成功 |
| 4 | `DELETE`，时间戳比 C 早 1 秒 | 当前时间 | 仍是 `C 写入`：更早的删除删不掉更新的数据 |

预期：四次操作都执行成功、没有报错，读到的值如上表变化。

分享要点：冲突按时间戳而不是到达顺序解决，所以所有节点和应用服务器都要配置 NTP 并监控时钟偏差。

## 15. 静态列与计数器

```powershell
dotnet run --project samples/ReservationService.Labs -- static-counter
```

1. 在 `lab_rooms_by_hotel` 写入 3 个房间，再只按分区键写一次静态列 `hotel_name`：3 行都能读到这个值。
2. 在 `lab_reservation_counts` 上执行 3 次 `total = total + 1`，结果为 3；对计数器表执行 `INSERT` 被拒绝。

预期：如上。

分享要点：静态列每个分区只存一份；计数器表只能有计数器列、只能 `UPDATE`，而且不是幂等的，超时重试可能重复计数。

## 16. 真实业务建模：报表数据比对

```powershell
dotnet run --project samples/ReservationService.Labs -- report-diff
```

按分享中“报表数据比对”的模型缩小规模实现：一个 `import_id`、一个报表类型、3 个月期间，每个期间有旧账端和柠檬云两份数据，每份 5 个科目、8 个金额字段。

| 表 | 分区键 | 聚簇列 | 作用 |
| --- | --- | --- | --- |
| `lab_report_periods_by_import` | `import_id` | `report_type, period_month` | 期间目录：两个来源的当前版本、状态、差异数 |
| `lab_report_rows_by_import` | `(import_id, report_type)` | `period_month, source, version, subject_code` | 两个来源的明细 |
| `lab_report_diffs_by_import` | `(import_id, report_type)` | `period_month, subject_code` | 预先算好的差异 |

流程：

1. 每个来源按期间写入新版本，用 LWT（`IF lemon_version = ?`）切换到新版本。
2. 两个来源都就绪后，读取这个期间的一段分区，按科目编码归并比较，结果写入差异表；再用 LWT（`IF legacy_version = ? AND lemon_version = ?`）确认比较期间版本没有变化。
3. 2 月份的柠檬云数据故意制造三种差异：金额差 0.01（`VALUE_MISMATCH`）、少一个科目（`ONLY_LEGACY`）、多一个科目（`ONLY_LEMON`）。
4. 柠檬云重新导入 2 月份正确数据：切换版本、重新比较，再用一条范围删除清掉旧版本。

预期：总览显示 2 月有 3 处差异，其余月份为 0；重新导入后全部为 0，2 月只剩新版本的 5 行；一次范围查询读回 3 个月 × 2 个来源 × 5 个科目 = 30 行。柠檬云金额是 4 位小数、旧账端是 2 位，按数值比较不会误报。

实现细节：差异表的 `diff_fields` 用 `frozen<set<text>>`，避免每次整体写入集合时产生墓碑；只有一方有数据时，缺失的一方不出现在 INSERT 的列清单里，既不写 null，也不需要对集合和 UDT 绑定 `Unset.Value`（C# 驱动不支持）。

## 可选：三节点集群与提示移交

单节点无法演示副本之间的修复。`compose.cluster.yaml` 提供一个独立的三节点集群（每个节点 512 MB 堆，不暴露端口、不挂数据卷），需要约 4 GB 空闲 Docker 内存。建议先停止单节点：

```powershell
docker compose stop
./scripts/cluster-demo.ps1 -Down
docker compose start
```

脚本依次执行：

1. 逐个启动三个节点，每个节点都要在 cass1 的 `nodetool status` 中显示为 `UN`（Up/Normal）后才启动下一个，首次约 4～6 分钟。如果发现上次中断留下的不完整集群，会先删除再重建；已有完整集群则直接复用。
2. 创建 RF=3 的 keyspace `demo`。
3. 停止 cass3。
4. 在 cass1 上写入：`QUORUM` 成功，`ALL` 失败（`Cannot achieve consistency level ALL`）。
5. 列出 cass1 的提示目录：出现一个以 cass3 Host ID 开头的 `.hints` 文件。
6. 启动 cass3，等待提示文件消失，即提示已投递。
7. 停止 cass1 和 cass2，只用 cass3 以 `ONE` 读取：读到了它掉线期间的那次写入。
8. 恢复集群；带 `-Down` 时删除集群。

说明：

- 节点必须逐个加入。两个新节点同时加入时可能分到冲突的 token，日志出现 `Bootstrap Token collision`，其中一个节点不会进入环，之后 QUORUM 写入会因为副本不足而失败。所以不要用 `docker compose -f compose.cluster.yaml up` 一次启动全部节点，请用脚本。
- 脚本会检查每条 CQL 的输出，出现错误立即停止并打印原因，不会把失败的写入报告为成功。
- 在 `cassandra:5.0.9` 上，`nodetool listpendinghints` 在提示文件存在时仍显示 “This node does not have any pending hints”，所以脚本直接查看提示目录。

分享要点：提示不计入一致性级别（ALL 照样失败）；默认只为掉线 3 小时以内的节点保存提示（`max_hint_window: 3h`），更久的要靠 `nodetool repair`，而且必须在 `gc_grace_seconds`（默认 10 天）内完成，否则已删除的数据可能复活。

## 推荐的分享演示顺序

按分享大纲的顺序，每项约 3～5 分钟：

| 大纲章节 | 实验 | 回答的问题 |
| --- | --- | --- |
| 2. 主要卖点 | `token` | 数据怎样分布到环上？ |
| 2.1 CQL | `overwrite`、`lww` | 熟悉的 INSERT / UPDATE 为什么不报错？冲突怎样解决？ |
| 4. 如何建模 | `anti-pattern`、`sai`、`static-counter` | 为什么查询要按主键设计？不建新表能按其他列查吗？ |
| 5. 多副本一致性 | `consistency`，可选 `scripts/cluster-demo.ps1` | 副本数和一致性级别怎样配合？掉线节点怎样补数据？ |
| 6. 架构细节 | `tracing`、`tombstones` | 数据库内部做了什么？删除为什么也有代价？ |
| 7. 真实业务 | `report-diff` | 一个真实的比对业务怎样建模？ |

## 扩展练习

建议按下面的顺序做，每项都先写出目标查询和预期行为：

1. 新增“按客人查询预订”。参考第 8 节的 SAI 对比，设计第三张表，明确修改客人时如何清理旧记录，并补充两条查询入口结果一致的集成测试。
2. 新增“查询指定日期所有在住预订”。先列出一笔三晚预订需要写入哪些日期，以及改期、取消需要清理哪些行。
3. 为 POST 设计客户端幂等键。分别讨论重复发送、写入超时、响应丢失和服务重启后的行为。
4. 设计冗余表对账与修复。先限定小数据集，确定权威数据来源、处理删除与并发写入的方法。
5. 在 `scripts/cluster-demo.ps1` 的三节点环境基础上，改用 .NET 驱动连接集群做停节点实验。记录哪些请求成功、哪些失败，以及恢复后如何验证数据。

这些练习没有混入主业务实现，可以在读懂当前两表模型后逐步增加。
