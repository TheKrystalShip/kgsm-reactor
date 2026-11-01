using System.Net;
using System.Net.Http.Headers;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using TheKrystalShip.Auth.Sessions;
using TheKrystalShip.Auth.Sessions.Testing;
using TheKrystalShip.Kgsm.Reactor.Actions;
using TheKrystalShip.Kgsm.Reactor.Rules.Composition;

namespace TheKrystalShip.Kgsm.Reactor.Tests;

/// <summary>
/// The reactor's write routes behind the sign-in provider's access token: no valid token is a 401, a token
/// without <see cref="ReactorActionIds.RulesWrite"/> is a 403, and with no provider configured the gate
/// answers 503. The handlers behind the gate are never reached by a refused request, so the proposal
/// service is registered to throw if one is.
/// </summary>
public sealed class WriteRouteGateTests
{
    private const string Audience = "kgsm";

    private static readonly (HttpMethod Method, string Path)[] GatedRoutes =
    [
        (HttpMethod.Post, "/proposals/handle-1/confirm"),
        (HttpMethod.Post, "/proposals/handle-1/dismiss"),
        (HttpMethod.Put, "/rules/some-rule"),
        (HttpMethod.Delete, "/rules/some-rule"),
    ];

    public static TheoryData<string, string> Routes()
    {
        var data = new TheoryData<string, string>();
        foreach ((HttpMethod method, string path) in GatedRoutes)
            data.Add(method.Method, path);
        return data;
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task NoToken_IsUnauthorized(string method, string path)
    {
        await using Host host = await Host.StartAsync(sessionsConfigured: true);

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Send(method, path, token: null)).StatusCode);
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task ATokenTheProviderDidNotSign_IsUnauthorized(string method, string path)
    {
        await using Host host = await Host.StartAsync(sessionsConfigured: true);
        string foreign = new TestIssuer(Audience).Mint("u1", actions: [ReactorActionIds.RulesWrite]);

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Send(method, path, foreign)).StatusCode);
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task ATokenWithoutTheAction_IsForbidden(string method, string path)
    {
        await using Host host = await Host.StartAsync(sessionsConfigured: true);
        string token = host.Issuer.Mint("u1", actions: ["kgsm:instances.read"]);

        Assert.Equal(HttpStatusCode.Forbidden, (await host.Send(method, path, token)).StatusCode);
    }

    [Fact]
    public async Task ATokenListingTheAction_ReachesTheHandler()
    {
        await using Host host = await Host.StartAsync(sessionsConfigured: true);
        string token = host.Issuer.Mint("u1", actions: [ReactorActionIds.RulesWrite]);

        // No such rule is stored, which only the handler can say.
        Assert.Equal(HttpStatusCode.NotFound, (await host.Send("DELETE", "/rules/some-rule", token)).StatusCode);
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task WithNoProviderConfigured_TheGatedRoutesAnswerServiceUnavailable(string method, string path)
    {
        await using Host host = await Host.StartAsync(sessionsConfigured: false);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await host.Send(method, path, token: null)).StatusCode);
    }

    private sealed class Host : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly HttpClient _client;

        public TestIssuer Issuer { get; }

        private Host(WebApplication app, TestIssuer issuer)
        {
            _app = app;
            _client = app.GetTestClient();
            Issuer = issuer;
        }

        public static async Task<Host> StartAsync(bool sessionsConfigured)
        {
            var issuer = new TestIssuer(Audience);
            WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseTestServer();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Sessions:Issuer"] = TestIssuer.DefaultIssuer,
                ["Sessions:Audience"] = Audience,
            });
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddSingleton(new RuleRegistry(string.Empty, NullLogger<RuleRegistry>.Instance));
            builder.Services.AddSingleton<ProposalService>(
                _ => throw new InvalidOperationException("a refused request reached the proposal service"));
            if (sessionsConfigured)
            {
                builder.Services.AddSessions();
                issuer.UseTestIssuer(builder.Services);
            }

            WebApplication app = builder.Build();
            Program.UseSessionsGate(app, sessionsConfigured);
            Program.MapWriteRoutes(app);
            await app.StartAsync();
            return new Host(app, issuer);
        }

        public Task<HttpResponseMessage> Send(string method, string path, string? token)
        {
            var request = new HttpRequestMessage(new HttpMethod(method), path);
            if (token is not null)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return _client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await _app.DisposeAsync();
        }
    }
}
