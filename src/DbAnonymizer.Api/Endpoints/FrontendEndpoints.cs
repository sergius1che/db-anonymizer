namespace DbAnonymizer.Api.Endpoints;

public static class FrontendEndpoints
{
    public static void MapFrontendEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var environment = endpoints.ServiceProvider.GetRequiredService<IWebHostEnvironment>();
        // Explicit UI routes preserve bookmarks without swallowing API or missing asset URLs.
        foreach (var page in new[] { "/", "/index.html", "/jobs.html", "/plan-review.html", "/report.html" })
            endpoints.MapGet(page, () => Results.File(
                Path.Combine(environment.WebRootPath, "app", "index.html"), "text/html"));
    }
}
