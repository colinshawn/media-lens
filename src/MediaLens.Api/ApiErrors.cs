using System.Globalization;
using MediaLens.Contracts;
using MediaLens.Http;
using MediaLens.Resolution;

namespace MediaLens.Api;

internal static class ApiErrors
{
    private const string RequestIdFormat = "N";

    public static void UseApiErrors(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            context.TraceIdentifier = Guid.NewGuid().ToString(RequestIdFormat);
            using var scope = app.Logger.BeginScope(new Dictionary<string, object>
            {
                ["requestId"] = context.TraceIdentifier
            });
            try
            {
                if (context.Request.Headers.ContainsKey(HttpDefaults.CookieHeader))
                {
                    await WriteAsync(context, StatusCodes.Status400BadRequest,
                        new ApiError(ErrorCodes.InvalidInput, "本接口不接受 Cookie 或身份输入。", false, null));
                    return;
                }

                if (context.Request.ContentLength > ApiDefaults.MaximumRequestBodyBytes)
                {
                    await WriteAsync(context, StatusCodes.Status413PayloadTooLarge,
                        new ApiError(ErrorCodes.InvalidInput, "请求正文超过大小限制。", false, null));
                    return;
                }

                await next(context);
            }
            catch (ResolutionException exception)
            {
                await WriteAsync(context, exception.StatusCode, exception.Error);
            }
            catch (BadHttpRequestException exception)
            {
                await WriteAsync(context, exception.StatusCode, MapStatus(exception.StatusCode));
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                // 调用方已离开，继续传播到上游；不能尝试向已取消的连接写入 500。
                context.Abort();
            }
            catch (Exception exception)
            {
                app.Logger.LogError("解析请求失败，异常类型：{ExceptionType}", exception.GetType().Name);
                await WriteAsync(context, StatusCodes.Status500InternalServerError,
                    MapStatus(StatusCodes.Status500InternalServerError));
            }
        });
        app.UseStatusCodePages(async status =>
            await WriteAsync(status.HttpContext, status.HttpContext.Response.StatusCode,
                MapStatus(status.HttpContext.Response.StatusCode)));
    }

    private static Task WriteAsync(HttpContext context, int status, ApiError error)
    {
        if (context.Response.HasStarted || context.RequestAborted.IsCancellationRequested)
        {
            return Task.CompletedTask;
        }

        context.Response.StatusCode = status;
        if (error.RetryAfterSeconds is { } wait)
        {
            context.Response.Headers.RetryAfter = wait.ToString(CultureInfo.InvariantCulture);
        }

        return context.Response.WriteAsJsonAsync(
            new ApiErrorResponse(context.TraceIdentifier, error),
            MediaLensJsonContext.Default.ApiErrorResponse,
            contentType: null,
            cancellationToken: context.RequestAborted);
    }

    private static ApiError MapStatus(int status) => status switch
    {
        StatusCodes.Status404NotFound => new ApiError(ErrorCodes.EndpointNotFound, "接口路径不存在。", false, null),
        StatusCodes.Status405MethodNotAllowed => new ApiError(ErrorCodes.MethodNotAllowed, "该接口不支持此 HTTP 方法。", false, null),
        StatusCodes.Status415UnsupportedMediaType => new ApiError(ErrorCodes.UnsupportedMediaType, "请使用 application/json 请求正文。", false, null),
        StatusCodes.Status500InternalServerError => new ApiError(ErrorCodes.InternalError, "服务内部发生错误。", false, null),
        _ => new ApiError(ErrorCodes.InvalidInput, "请求内容或格式不合法。", false, null)
    };
}
