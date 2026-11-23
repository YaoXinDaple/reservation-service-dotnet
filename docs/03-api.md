# API 操作说明

默认地址为 `http://localhost:5080`。Swagger UI 在 `/swagger`，完整请求示例在 `requests/reservations.http`。

## 路由

| 方法 | 路径 | 成功响应 |
| --- | --- | --- |
| POST | `/api/v1/reservations` | 201，预订 JSON 和 Location |
| GET | `/api/v1/reservations/{confirmationNumber}` | 200，预订 JSON |
| PUT | `/api/v1/reservations/{confirmationNumber}` | 200，修改后的预订 JSON |
| DELETE | `/api/v1/reservations/{confirmationNumber}` | 204，无响应体 |
| GET | `/api/v1/reservations/findByHotelAndDate` | 200，分页结果 |
| GET | `/api/v1/reservations` | 200，分页结果 |
| GET | `/health` | 200 Healthy；数据库不可用时 503 |

确认号必须是合法 GUID 字符串。不存在的预订返回 404；PUT 更新不存在的确认号也返回 404，不会创建一笔新记录。删除同一笔预订时，第一次返回 204，之后返回 404。

## 创建与修改

POST 与 PUT 使用相同请求体：

```json
{
  "hotelId": "H_BEIJING",
  "startDate": "2026-11-06",
  "endDate": "2026-11-09",
  "roomNumber": 101,
  "guestId": "a2792706-3c1a-43da-a375-d4cfa471de5e"
}
```

所有字段必填。hotelId 最长 64 个字符，去除首尾空白并保留大小写；endDate 必须晚于 startDate；roomNumber 为 1 到 32767 的整数；guestId 必须是非空 GUID。没有禁止历史日期，便于重复练习。

POST 成功后，响应会多出服务端生成的 `confirmationNumber`。PUT 是完整替换，必须提交全部业务字段，确认号保持不变。

更换酒店、入住日或房号时，API 会维护旧查询位置和新查询位置。允许同房同日、跨日重叠的多笔预订，服务不会将房号当成唯一库存。

## 酒店日期查询

```http
GET /api/v1/reservations/findByHotelAndDate?hotelId=H_BEIJING&date=2026-11-06&pageSize=2
```

hotelId 和 date 必填。date 是入住开始日，不是“某天是否仍在住”。缺少或无法解析日期时返回 400。

结果结构：

```json
{
  "items": [],
  "nextCursor": null
}
```

`items` 中的元素与按确认号查询的预订结构相同。有下一页时，nextCursor 是受保护的字符串，把它作为 `cursor` 查询参数传回相同接口即可。建议使用客户端的 query 参数编码功能，例如 .NET `QueryHelpers.AddQueryString` 或 PowerShell 的 `[Uri]::EscapeDataString()`。

pageSize 默认为 20，允许 1 到 100。游标绑定酒店、日期、pageSize、keyspace，且在 30 分钟后过期。改变条件或调整 pageSize 时，重新获取第一页。

酒店日期表按房号升序排列，同房号内按确认号字符串升序。该顺序不代表预订创建时间。

## 全表浏览

```http
GET /api/v1/reservations?pageSize=20
```

返回相同的分页结构。全表浏览没有日期或房号的全局排序，也没有 totalCount、pageNumber、totalPages。该接口用于观察小数据集，生产场景需要针对具体检索条件重新建模。

## 错误响应

错误采用 Problem Details JSON。字段验证错误包含 `errors`；请求携带错误或过期的游标返回 400。

| 状态 | 含义 | 处理方式 |
| --- | --- | --- |
| 400 | 参数、日期、GUID 或游标无效 | 修正输入；游标失效时重新从第一页查询 |
| 404 | 确认号不存在 | 检查确认号或重新获取预订 |
| 503 | Cassandra 暂不可用 | 查看 Docker 与 API 日志 |
| 504 | 数据库请求超时 | 先核实是否已完成写入 |
| 500 | 未预期的服务端错误 | 使用 traceId 查服务器日志 |

POST 生成新确认号，没有实现客户端幂等键。如果写入成功但响应丢失，再次提交相同 POST 会生成另一笔预订。不要把请求超时直接解释成“数据库没有写入”。
