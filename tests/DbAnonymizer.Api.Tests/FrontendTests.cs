using System.Net;
using System.Text.RegularExpressions;
using DbAnonymizer.Api.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DbAnonymizer.Api.Tests;

public sealed class FrontendTests : IDisposable
{
    private readonly IHost _host;
    private readonly HttpClient _client;

    public FrontendTests()
    {
        // The API project copies its generated frontend to the test output on build.
        _host = new HostBuilder().ConfigureWebHost(web => web.UseTestServer()
            .UseContentRoot(AppContext.BaseDirectory)
            .UseWebRoot(Path.Combine(AppContext.BaseDirectory, "wwwroot"))
            .ConfigureServices(services => services.AddRouting())
            .Configure(app =>
            {
                app.UseStaticFiles();
                app.UseRouting();
                app.UseEndpoints(endpoints => endpoints.MapFrontendEndpoints());
            })).Start();
        _client = _host.GetTestClient();
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/index.html")]
    [InlineData("/jobs.html")]
    [InlineData("/plan-review.html?id=test")]
    [InlineData("/report.html?id=test")]
    public async Task Ui_routes_serve_React_with_loadable_assets(string path)
    {
        var response = await _client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("id=\"root\"", html);
        var assets = Regex.Matches(html, "(?:src|href)=\"(/app/assets/[^\"]+)\"");
        Assert.NotEmpty(assets);
        foreach (Match asset in assets)
        {
            var assetResponse = await _client.GetAsync(asset.Groups[1].Value);
            Assert.Equal(HttpStatusCode.OK, assetResponse.StatusCode);
            Assert.NotEqual("text/html", assetResponse.Content.Headers.ContentType?.MediaType);
        }
    }

    [Theory]
    [InlineData("/jobs/unknown")]
    [InlineData("/plans/unknown")]
    [InlineData("/reports/unknown")]
    [InlineData("/app/assets/missing.js")]
    public async Task Unknown_urls_do_not_return_the_spa(string path)
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync(path)).StatusCode);
    }

    public void Dispose() { _client.Dispose(); _host.Dispose(); }
}
