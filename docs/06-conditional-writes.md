# IF NOT EXISTS 与 IF EXISTS

这两类语句需要区分 schema 操作和预订数据操作。本项目把原项目中的用法对应到独立可执行的 .NET 示例，并补充数据更新与删除的条件写入。

## 原项目实际包含什么

| 来源 | 原用法 | .NET 对应入口 |
| --- | --- | --- |
| master 的 CassandraConfiguration | `createKeyspace(...).ifNotExists()` | `schema-exists`，重复创建一次性 keyspace |
| master 的 CassandraConfiguration | `dropKeyspace(...).ifExists()` | `schema-exists`，重复删除一次性 keyspace |
| lightweight-transaction-solution 的 ReservationRepository | 酒店日期预订 INSERT 的 `.ifNotExists()` | `if-not-exists`，相同酒店、入住日、房号的重复插入 |
| 本次补充的数据条件操作 | `UPDATE/DELETE ... IF EXISTS` | `if-exists`，记录存在/不存在两个分支 |

原项目的 [配置类](https://github.com/jeffreyscarpenter/reservation-service/blob/20a6384a39eaa87e1c4e143509ac67011875e218/src/main/java/dev/cassandraguide/conf/CassandraConfiguration.java) 中，`IF EXISTS` 用于删除 keyspace；[LWT 练习答案](https://github.com/jeffreyscarpenter/reservation-service/blob/8bc275c124bed1fa627176ffd1944eb096840c79/src/main/java/dev/cassandraguide/repository/ReservationRepository.java) 中的预订唯一性采用 `INSERT ... IF NOT EXISTS`。这里没有把新增的条件 UPDATE/DELETE 说成原项目已经具备的实现。

此前 .NET 版已有建表时的 `IF NOT EXISTS` 和 claim_id 条件竞争实验，但没有完整的预订条件写入示例。本次将 LWT 示例改为预订模型，并加入单独命令，方便直接定位。

## 怎么运行

在项目根目录执行：

```powershell
docker compose up -d --wait --wait-timeout 600

dotnet run --project samples/ReservationService.Labs -- if-not-exists
dotnet run --project samples/ReservationService.Labs -- if-exists
dotnet run --project samples/ReservationService.Labs -- schema-exists
```

一次执行全部预订条件示例及并发竞争：

```powershell
dotnet run --project samples/ReservationService.Labs -- lwt
```

`all` 会执行全部十六项实验，其中 `lwt` 已包含上述两种数据条件示例。

## 预订实验的主键

`ConditionalReservationExamples.cs` 在实验 keyspace 中建立：

```cql
CREATE TABLE IF NOT EXISTS lab_reservations_by_hotel_date (
    hotel_id text,
    start_date date,
    room_number smallint,
    confirmation_number text,
    end_date date,
    guest_id uuid,
    PRIMARY KEY ((hotel_id, start_date), room_number)
);
```

它沿用原 LWT 分支的完整主键：酒店、入住日、房号。同一天同房间只能保存一行；新的确认号不改变这行的主键。主 API 的酒店日期表还把确认号放进主键，允许独立保存多笔重叠预订。两种模型分别服务此前确认的主业务与本次唯一性教学。

## INSERT IF NOT EXISTS

语句只在完整主键不存在时插入。示例先插入预订 A，再用相同主键、不同确认号和客人尝试插入预订 B。

| 操作 | `[applied]` | 查询结果 |
| --- | --- | --- |
| 首次插入 A | true | 保存 A |
| 相同主键插入 B | false | 仍为 A，任何业务字段都没有被 B 覆盖 |

接着，`lwt` 发起八个相同主键的并发条件插入，检查恰好一个成功，并核对数据库保存的完整记录确实属于这个成功请求。不同房号或不同入住日属于不同主键，可以分别插入；它不提供跨日期区间的冲突检测。

## UPDATE / DELETE IF EXISTS

普通 Cassandra UPDATE 具有 upsert 行为。示例显式加上 `IF EXISTS`，让不存在的记录保持不存在。

```cql
UPDATE lab_reservations_by_hotel_date
SET end_date = ?, guest_id = ?
WHERE hotel_id = ? AND start_date = ? AND room_number = ? IF EXISTS;

DELETE FROM lab_reservations_by_hotel_date
WHERE hotel_id = ? AND start_date = ? AND room_number = ? IF EXISTS;
```

更新只修改离店日与客人，不改主键或确认号。

| 操作 | `[applied]` | 数据结果 |
| --- | --- | --- |
| 更新不存在的预订 | false | 不创建新行 |
| 更新已存在的预订 | true | 离店日与客人更新，确认号保留 |
| 删除已存在的预订 | true | 行被删除 |
| 再次删除同一主键 | false | 行仍不存在 |

`IF EXISTS` 只验证行是否存在，不验证当前确认号、客人或版本是否仍与调用者预期一致。需要比较这些值时，应另外设计条件，例如 `IF confirmation_number = ?`。

## C# 如何处理结果

和 Java 练习一样，先 Prepare，再为每个请求 Bind。执行条件写入时设置串行一致性，并读取结果行里的 `[applied]`：

```csharp
statement.SetSerialConsistencyLevel(ConsistencyLevel.LocalSerial);
var rows = await session.ExecuteAsync(statement);
var applied = rows.Single().GetValue<bool>("[applied]");
```

条件不成立通常是正常的 false 结果，不是抛出的异常。仅仅 `await ExecuteAsync` 没有异常，不能证明这次条件写入成功。示例随后查询数据，验证失败的条件写入没有造成覆盖或意外创建。

## Schema 中的存在性判断

`schema-exists` 使用随机命名的一次性 keyspace，执行两次 `CREATE KEYSPACE IF NOT EXISTS`，再执行两次 `DROP KEYSPACE IF EXISTS`。重复执行时，schema 已经处于目标状态也可以成功。

程序从 `system_schema.keyspaces` 读取实际存在状态，不读取 `[applied]`。这里展示的是 DDL 的重复执行语义。数据条件写入的结果处理与性能成本属于上一节的 LWT 范围。

## 两张业务表怎样处理

这些条件写入示例使用独立实验表。主 API 继续通过小型 LOGGED BATCH 维护两张预订查询表。

Cassandra 的条件 BATCH 不能跨表或跨分区。原 LWT 分支把条件插入与另一张查询表的普通写入分开执行，这不是两表原子事务；条件插入未成功时，调用方需要正确处理结果，不能继续假设整笔业务创建成功。扩展到正式业务时，需要同时设计失败恢复、幂等与冗余查询表维护，见 [一致性说明](04-consistency.md)。

## 自动验证

```powershell
dotnet test ReservationService.sln --configuration Release
```

`ConditionalReservationTests` 使用真实 Docker Cassandra 验证重复插入不覆盖原数据、不同主键可插入、条件更新的成功与失败分支、条件删除的成功与失败分支，以及八个并发插入只保留一个获胜者。
