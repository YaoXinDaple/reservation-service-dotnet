using Cassandra;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace ReservationService.Api;

public sealed class ApiExceptionHandler(IProblemDetailsService problems, ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title, detail) = exception switch
        {
            InvalidCursorException => (400, "分页游标无效", "请使用此查询返回的游标，保持查询条件和 pageSize 不变。游标有效期为 30 分钟。"),
            NoHostAvailableException or UnavailableException => (503, "Cassandra 暂不可用", "请检查 Docker 和数据库状态。"),
            ReadTimeoutException or WriteTimeoutException or OperationTimedOutException =>
                (504, "Cassandra 请求超时", "写入超时不代表写入未发生。请核实当前数据，避免直接重复创建预订。"),
            _ => (500, "服务器处理失败", "请查看服务器日志，并使用 traceId 定位问题。")
        };
        if (status >= 500) logger.LogError(exception, "Request failed: {TraceId}", context.TraceIdentifier);
        context.Response.StatusCode = status;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = new ProblemDetails { Status = status, Title = title, Detail = detail }
        });
    }
}
