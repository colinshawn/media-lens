namespace MediaLens.Api;

internal sealed record ApiTokenRequired;

internal static class ApiAccessControl
{
    public static void UseApiAccessControl(this WebApplication app, SecretToken secret)
    {
        app.Use(async (context, next) =>
        {
            // 在路由匹配后、端点委托绑定正文前执行；404/405 保留框架行为。
            if (context.GetEndpoint()?.Metadata.GetMetadata<ApiTokenRequired>() is not null &&
                !secret.Authenticate(context.Request.Headers.Authorization))
            {
                app.Logger.LogInformation("API 访问凭证无效。");
                context.Response.Headers.WWWAuthenticate = SecretToken.Scheme;
                await ApiErrors.WriteAsync(context, StatusCodes.Status401Unauthorized);
                return;
            }

            await next(context);
        });
    }
}
