# 从 HTTP 请求跟到 Cassandra

先打开 `ReservationsController.Create`，再打开 `CassandraReservationRepository.CreateAsync`。执行一次 POST，就能跟完 HTTP 参数验证、业务数据映射、绑定 CQL 参数、发送 BATCH 和返回 201 的流程。

## 生命周期

`Program.cs` 将 `CassandraDatabase` 和 Repository 注册为单例。`DatabaseInitializer` 在应用开始处理请求前连接数据库、创建缺失的表，并准备所有常用语句。

```text
应用启动
  → 创建 Cluster
  → ConnectAsync 得到 Session
  → 选择 keyspace 并创建缺失表
  → PrepareAsync 准备七条常用语句
  → 开始接受 HTTP 请求
```

Cluster 管理拓扑和连接，Session 执行请求。每次 HTTP 请求创建一个 Cluster 会反复建立连接、发现节点并消耗资源。本项目一直复用单例对象，应用关闭后释放。

初始化失败会让 API 启动失败，日志保留原因。启动脚本先等待 Docker 健康检查成功，避免 API 在数据库尚未就绪时连接。

## 配置如何传递

API 从 `appsettings.json` 的 `Cassandra` 节绑定 `CassandraOptions`，也支持环境变量覆盖。`CassandraDatabase` 对 keyspace 名称做白名单验证，因为 CQL 不能把表名和 keyspace 标识符作为普通业务参数绑定。

所有预订业务字段都使用 Prepared Statement 的绑定参数。代码里只有固定模式定义与经过验证的 keyspace 名称参与 CQL 拼接。

本地 keyspace 使用 `NetworkTopologyStrategy`，`datacenter1` 的副本数为 1，默认读写一致性为 `LOCAL_QUORUM`。副本数为 1 时，一个副本即可满足该级别；这不能用于验证多节点的容错行为。

## Prepare 一次，Bind 多次

启动时：

```csharp
_find = await session.PrepareAsync(
    "SELECT confirmation_number, hotel_id, start_date, end_date, room_number, guest_id " +
    "FROM reservations_by_confirmation WHERE confirmation_number = ?");
```

请求时：

```csharp
var statement = _find.Bind(confirmationNumber.ToString());
var rows = await database.Session.ExecuteAsync(statement);
```

`PrepareAsync` 准备语句，`Bind` 把本次请求的值交给驱动编码。同一个 PreparedStatement 可以服务多次请求，每次创建独立 BoundStatement。不要把同一个可变 BoundStatement 在多个请求间复用。

`O'Reilly` 这样的文本无需自己拼接转义符。`prepared` 实验会将它写入再读回，直接验证参数绑定。

## DateOnly 与 Cassandra.LocalDate

业务层使用 `DateOnly`，驱动层转换为：

```csharp
new Cassandra.LocalDate(value.Year, value.Month, value.Day)
```

读取时反向转换。`date` 表示日历日期，`timestamp` 表示瞬时时间；预订入住日用前者，不依赖服务器时区。

驱动是异步 API，但这个版本的核心执行接口没有与 ASP.NET Core 请求取消完全对应的 CancellationToken 参数。本项目没有在连接上伪造“客户端取消就撤销 Cassandra 写入”的语义。关闭浏览器或 HTTP 请求超时后，已经发送的写入仍可能成功。

## 显式分页

默认情况下，驱动可以在枚举 RowSet 时自动取下一页。API 如果直接 `ToList()`，可能把整个查询结果加载到内存。

本项目显式关闭自动翻页：

```csharp
statement.SetAutoPage(false).SetPageSize(pageSize);
if (pagingState is not null)
    statement.SetPagingState(pagingState);

var rows = await session.ExecuteAsync(statement);
var items = rows.Select(Map).ToList();
var nextState = rows.PagingState;
```

`PagingState` 是驱动继续读取的状态，不能当作页码。最后一页没有后续状态时，API 返回 `nextCursor: null`。分页不是数据库快照，遍历期间修改数据会影响观察到的结果。

API 使用 ASP.NET Core Data Protection 加密并验证分页状态，同时绑定查询范围和 pageSize，设置 30 分钟有效期。酒店、日期、列表接口或 pageSize 发生变化，都需要从第一页开始。保护密钥由本机 Data Protection 管理；更换机器、删除密钥或重新配置密钥存储后，旧游标可能无法使用。

## 分层为什么只有这些

- Core 定义预订数据与 Repository 接口，不依赖 Cassandra 或 ASP.NET Core。
- Cassandra 层负责连接、CQL、类型转换和冗余表维护。
- API 层负责 HTTP 状态、输入验证、分页游标和错误响应。

这里没有通用 SQL 风格 Repository、自动映射实体关系或工作单元。Cassandra 的查询与表结构关系需要在代码中明确表达，读 Repository 时就能看到实际访问哪个分区。

参考：[驱动 Prepared Statement 文档](https://github.com/datastax/csharp-driver/blob/master/doc/features/components/core/statements/prepared/README.md)、[驱动分页文档](https://github.com/datastax/csharp-driver/blob/master/doc/features/paging/README.md)、[ASP.NET Core OpenAPI](https://learn.microsoft.com/aspnet/core/fundamentals/openapi/overview?view=aspnetcore-10.0)。
