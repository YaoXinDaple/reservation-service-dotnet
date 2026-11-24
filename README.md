# Reservation Service .NET：用 .NET 10 学习 Cassandra

这是一个可以在本机运行的酒店预订 API，参考 [Jeff Carpenter 的 reservation-service](https://github.com/jeffreyscarpenter/reservation-service)，基准提交为 `20a6384a39eaa87e1c4e143509ac67011875e218`。

通过创建、查询、修改和删除预订，学习 C# 驱动如何连接 Cassandra，以及为什么 Cassandra 的表需要围绕查询设计。API 在本机运行，数据库由 Docker Compose 提供。

包含 28 项真实数据库集成测试、16 项可执行实验和中文学习文档。实验覆盖查询驱动建模、Prepared Statement、分页、TTL、LWT、BATCH、SAI、墓碑和一致性级别。

本项目用于本机学习与演示，API 未实现身份认证，数据库未配置认证或 TLS。示例数据由代码生成。

## 快速启动

需要 .NET SDK **10.0.302** 和 Docker Compose v2 或更高版本。Windows 可使用已启动的 Docker Desktop Linux 引擎。`global.json` 指定 SDK 版本，允许同一 feature band 的后续补丁。首次启动需要下载 Cassandra 镜像，通常需要几分钟；Docker 建议至少分配 4 GB 内存。

在本目录打开 PowerShell：

```powershell
./scripts/start.ps1
```

也可以分步执行，适用于 PowerShell、Bash 或 zsh：

```powershell
docker compose up -d --wait --wait-timeout 600
dotnet run --project src/ReservationService.Api --launch-profile http
```

看到 `Cassandra is ready` 后打开：

- API 操作页面：<http://localhost:5080/swagger>
- OpenAPI 文档：<http://localhost:5080/openapi/v1.json>
- 数据库健康检查：<http://localhost:5080/health>

Swagger 与 OpenAPI 在 Development 环境开放。启动配置已设置该环境。Swagger UI 资源随 NuGet 包提供，不需要从公共 CDN 加载。

另开一个 PowerShell 窗口，在本目录写入三笔示例预订：

```powershell
./scripts/seed.ps1
```

前两笔的酒店、入住日和房号相同，但确认号不同。查询 `H_BEIJING`、`2026-11-06` 应得到两笔，查询 `2026-11-07` 应得到第三笔。每次运行脚本都会新增三笔，不会清空已有数据。

也可以用 [requests/reservations.http](requests/reservations.http) 逐个发送请求。创建后复制响应中的 `confirmationNumber`，填写文件顶部变量，即可查询、修改和删除这笔预订。

## 验证

```powershell
./scripts/check.ps1
```

脚本启动 Cassandra，运行真实数据库集成测试，再运行全部十六项实验。集成测试使用随机独立 keyspace，结束后删除该测试 keyspace；数据库不可用时会失败，不会跳过。它不会清理 API 的 `reservation_service` 数据。

也可以单独运行：

```powershell
dotnet test ReservationService.sln
dotnet run --project samples/ReservationService.Labs -- all
```

GitHub Actions 会在每次推送和 Pull Request 时构建项目，使用真实 Cassandra 运行集成测试与全部实验。配置见 [CI 工作流](.github/workflows/ci.yml)。

`NuGet.Config` 指定 nuget.org 为漏洞审计来源，包下载仍使用本机配置的源。还原依赖时会审计直接和间接依赖，审计警告按错误处理。`Microsoft.OpenApi` 显式引用修复版 `2.7.5`，用于修复 [GHSA-v5pm-xwqc-g5wc](https://github.com/advisories/GHSA-v5pm-xwqc-g5wc)。

## 技术分享现场演示

演示前做一次准备和彩排，演示过程中每讲到一节，再现场运行对应的实验。实验只需要 Cassandra，不需要 API；`./scripts/start.ps1` 会在前台运行 API 并占住终端，只在需要演示 Swagger 或 HTTP 接口时使用。

以下命令可以直接配合 [实验文档](docs/05-labs.md) 演示，仓库不包含幻灯片。

### 演示前（建议提前 15 分钟）

```powershell
# 1. 打开 Docker Desktop，启动 Cassandra（只启动数据库）
docker compose up -d --wait --wait-timeout 600

# 2. 彩排：运行集成测试和全部 16 项实验，约 2～3 分钟
./scripts/check.ps1

# 3. 编译一次，现场运行时加 --no-build，省掉编译等待
dotnet build samples/ReservationService.Labs

# 4. 在演示用的 PowerShell 窗口里定义短命令
function lab($name) { dotnet run --no-build --project samples/ReservationService.Labs -- $name }
```

可选：想在 `tracing` 实验中展示布隆过滤器跳过 SSTable，先执行 `docker compose exec cassandra nodetool flush reservation_labs`。

### 演示过程中

| 主题 | 现场命令 |
| --- | --- |
| 数据模型、token 环 | `lab token` |
| CQL 写法相同、效果不同 | `lab overwrite`、`lab lww` |
| 轻量级事务 | `lab lwt` |
| 查询建模 | `lab anti-pattern`、`lab static-counter`、`lab sai` |
| 一致性级别 | `lab consistency` |
| 提示移交 | `./scripts/cluster-demo.ps1`（可选，见下文） |
| 架构细节 | `lab tracing`、`lab tombstones`、`lab batch` |
| 报表数据比对建模 | `lab report-diff` |

除 `cluster-demo.ps1` 外，每个实验几秒内完成，结果会自动校验并输出 PASS。各实验的预期输出和讲解要点见 [十六项实验](docs/05-labs.md)。

### 三节点演示的两种做法

- 演示前运行脚本并保存输出，现场根据 [提示移交实验说明](docs/05-labs.md) 讲解。
- 现场运行：分享前先执行一次 `./scripts/cluster-demo.ps1`（不加 `-Down`），首次约 4～6 分钟，集群保持运行；现场再执行一次约 2 分钟。需要约 4 GB 空闲 Docker 内存。单节点与三节点集群同时运行的情况尚未验证，内存不足时先 `docker compose stop` 停掉单节点，演示完再 `docker compose start`。

## 代码结构

```text
src/
  ReservationService.Core/          业务数据结构与 Repository 接口
  ReservationService.Cassandra/     连接、建表、Prepared Statement、分页、BATCH
  ReservationService.Api/           HTTP、验证、错误处理、游标保护、OpenAPI
samples/ReservationService.Labs/    独立的 Cassandra 控制台实验
tests/ReservationService.IntegrationTests/
database/schema.cql                 实际使用的两张表
requests/reservations.http          可以逐个执行的 API 请求
compose.cluster.yaml                可选的三节点集群（提示移交演示）
scripts/                           启动、验证与示例数据脚本
docs/                              中文学习文档
```

固定依赖：.NET 10、`CassandraCSharpDriver 3.23.0`、`cassandra:5.0.9`。C# 驱动的包以 .NET Standard 2.0 为目标，本项目通过 .NET 10 与真实 Cassandra 的集成测试验证实际组合。[驱动包说明](https://www.nuget.org/packages/CassandraCSharpDriver/3.23.0)

## 学习顺序

1. 阅读 [数据建模](docs/01-data-model.md)，先弄清查询与主键的关系。
2. 跟读 [C# 驱动与请求流程](docs/02-dotnet-driver.md)，在 Repository 中打断点。
3. 使用 [API 说明](docs/03-api.md) 修改酒店、日期和房间，观察两张表。
4. 阅读 [一致性与并发边界](docs/04-consistency.md)，理解 BATCH 能解决什么。
5. 完成 [十六项独立实验](docs/05-labs.md)：前六项对应原项目，其余十项（反模式查询、SAI、tracing、墓碑、一致性级别、token、静默覆盖、最后写入者胜、静态列与计数器、报表数据比对）按技术分享大纲的顺序设计，适合现场演示。
6. 对照 [IF NOT EXISTS 与 IF EXISTS](docs/06-conditional-writes.md)，运行预订条件插入、条件更新、条件删除和 schema 重复创建/删除示例。
7. 可选：运行 `./scripts/cluster-demo.ps1`，在三节点集群上观察提示移交（需要约 4 GB 空闲 Docker 内存）。

## 与原项目的对应关系

| 原项目 | .NET 版本 |
| --- | --- |
| Spring Controller | ASP.NET Core Controller |
| Java Driver Session | 单例 C# Driver Session |
| Java LocalDate | HTTP/业务层 DateOnly，数据库层 Cassandra.LocalDate |
| 两张预订查询表 | 保留，同时在酒店日期表主键中加入确认号 |
| Prepared Statement | 启动时准备，执行时重复 Bind |
| LOGGED BATCH | 小型关联写入，同时维护两张查询表 |
| 全量列表 | 游标分页，无全局排序与总页数 |
| 更新时遗留旧酒店日期记录 | 读取旧主键，删除旧查询记录，再写入新位置 |
| 示例分支 | 独立可执行的 prepared、paging、ttl、lwt、batch、schema-exists 实验 |
| LWT 分支的预订条件插入 | `if-not-exists`，沿用原分区键与房号聚簇键 |
| Schema 创建与清理中的存在性判断 | `schema-exists`，独立 keyspace 中验证 CREATE / DROP |

单独执行条件示例：

```powershell
dotnet run --project samples/ReservationService.Labs -- if-not-exists
dotnet run --project samples/ReservationService.Labs -- if-exists
dotnet run --project samples/ReservationService.Labs -- schema-exists
```

`lwt` 和 `all` 也会覆盖条件插入、条件更新、条件删除，以及八个并发预订竞争。实现位于 `samples/ReservationService.Labs/ConditionalReservationExamples.cs`，每次条件写入都会读取 `[applied]`，并校验最终数据。

业务范围包括预订的 CRUD，以及按确认号、酒店和入住日查询。允许预订时间重叠，酒店日期查询的日期含义是“开始入住的日期”。不涉及酒店管理、客人管理、登录、支付或房态库存。

## 配置与数据保存

默认配置在 `src/ReservationService.Api/appsettings.json`。环境变量采用 ASP.NET Core 的双下划线规则：

```powershell
$env:Cassandra__ContactPoints__0 = '127.0.0.1'
$env:Cassandra__Port = '9042'
$env:Cassandra__Keyspace = 'reservation_service'
```

API 启动时创建缺失的 keyspace 和表。它不会通过 `IF NOT EXISTS` 自动迁移已存在表的主键；改数据模型时需要显式迁移。

Compose 将 9042 绑定到 `127.0.0.1`，使用命名卷保存数据：

```powershell
docker compose stop       # 停止数据库，保留容器和数据
docker compose start      # 再次启动
docker compose down       # 删除本项目容器与网络，保留数据卷
```

本示例按本地单 API 实例设计。进程内串行化修改可以减少读旧值后更新查询表的竞态；多实例并发、写入超时和跨分区可见性仍有边界，见 [一致性文档](docs/04-consistency.md)。

## 常见启动问题

- Docker 连接失败或返回 500：先打开 Docker Desktop，确认 Linux 引擎可用，再运行 `docker version`。
- 首次启动较慢：运行 `docker compose ps`、`docker compose logs --tail 100 cassandra`，等待状态变为 healthy。
- API 提示没有可用主机：先完成 `docker compose up -d --wait --wait-timeout 600`，再启动 API。
- 9042 被占用：修改 Compose 的宿主机端口，并同步修改 API 配置；实验支持同名 `Cassandra__Port` 环境变量。
- 5080 被占用：使用 `dotnet run --project src/ReservationService.Api --no-launch-profile -- --urls http://localhost:5081 --environment Development`，并调整请求文件与 seed 脚本的地址。
- PowerShell 阻止运行脚本：使用上面的分步命令即可完成相同操作。

## 发布到 GitHub

仓库创建、首次推送、自动验证与许可证状态见 [发布说明](docs/07-github.md)。
