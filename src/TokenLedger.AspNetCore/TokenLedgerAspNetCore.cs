using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace TokenLedger.AspNetCore;

/// <summary>Adds TokenLedger to an ASP.NET Core application under test.</summary>
public static class TokenLedgerServiceCollectionExtensions
{
    /// <summary>
    /// Adds a middleware, first in the pipeline, that attributes each model call to the endpoint handling the
    /// request (<c>POST api/support/chat</c>) and connects requests sent through <see cref="LedgerHttpHandler"/>
    /// to their scenario. Call it from the test host (<c>WebApplicationFactory.ConfigureWebHost</c>,
    /// <c>ConfigureServices</c>); in an application with no open scenario it does nothing.
    /// </summary>
    public static IServiceCollection AddTokenLedger(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IStartupFilter, LedgerStartupFilter>());
        return services;
    }
}

/// <summary>
/// Sends the open scenario with each request, so the application under test measures the request's model calls
/// in that scenario, even when the request does not run in the test's async flow (a real host, or TestServer
/// without <c>PreserveExecutionContext</c>).
/// </summary>
/// <example>
/// <code>
/// await using var ledger = Ledger.Scenario("support/chat");
/// var client = factory.CreateDefaultClient(new LedgerHttpHandler(ledger));
/// </code>
/// </example>
public sealed class LedgerHttpHandler : DelegatingHandler
{
    /// <summary>The request header that carries the scenario.</summary>
    public const string HeaderName = "TokenLedger-Scenario";

    private readonly LedgerScenario _scenario;

    /// <summary>For <c>WebApplicationFactory.CreateDefaultClient</c>, which sets the inner handler.</summary>
    public LedgerHttpHandler(LedgerScenario scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        _scenario = scenario;
    }

    /// <summary>For an <see cref="HttpClient"/> of your own: <c>new HttpClient(new LedgerHttpHandler(ledger, new SocketsHttpHandler()))</c>.</summary>
    public LedgerHttpHandler(LedgerScenario scenario, HttpMessageHandler innerHandler) : this(scenario)
    {
        ArgumentNullException.ThrowIfNull(innerHandler);
        InnerHandler = innerHandler;
    }

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Remove(HeaderName);
        request.Headers.TryAddWithoutValidation(HeaderName, _scenario.Id);
        return base.SendAsync(request, cancellationToken);
    }
}

internal sealed class LedgerStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(LedgerMiddleware.InvokeAsync);
        next(app);
    };
}

internal static class LedgerMiddleware
{
    public static async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        // A request that names an open scenario joins it; one that runs in the test's flow already has it.
        IDisposable? scenario = null;
        if (context.Request.Headers.TryGetValue(LedgerHttpHandler.HeaderName, out var id) && Ledger.Find(id.ToString()) is { } named)
        {
            scenario = Ledger.Enter(named);
        }

        // Resolved when a call is measured: routing has chosen the endpoint by then.
        using (Ledger.Endpoint(() => Describe(context)))
        using (scenario)
        {
            await next(context);
        }
    }

    /// <summary><c>POST api/support/chat</c>: the method and the route pattern, so ids in the path do not split an endpoint.</summary>
    internal static string Describe(HttpContext context)
    {
        var route = context.GetEndpoint() is RouteEndpoint endpoint && endpoint.RoutePattern.RawText is { } pattern
            ? pattern
            : context.Request.Path.Value ?? "/";
        return $"{context.Request.Method} {route.Trim('/')}";
    }
}
