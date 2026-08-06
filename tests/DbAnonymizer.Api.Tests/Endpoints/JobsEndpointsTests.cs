// Endpoints/JobsEndpointsTests.cs
using System.Net;
using System.Net.Http.Json;
using DbAnonymizer.Core.Models;

namespace DbAnonymizer.Api.Tests.Endpoints;

public sealed class JobsEndpointsTests : IClassFixture<ApiTestFixture>
{
    private readonly ApiTestFixture _app;

    public JobsEndpointsTests(ApiTestFixture app)
    {
        _app = app;
    }

    [Fact]
    public async Task PostJobs_ReturnsCreated()
    {
        var client = _app.CreateClient();
        var response = await client.PostAsJsonAsync("/jobs", new
        {
            source = new { dumpFilePath = "/tmp/test.dump" }
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task GetJobs_ReturnsCreatedJob()
    {
        var client = _app.CreateClient();
        var postResponse = await client.PostAsJsonAsync("/jobs", new
        {
            source = new { dumpFilePath = "/tmp/test.dump" }
        });
        var created = await postResponse.Content.ReadFromJsonAsync<JobState>();
        Assert.NotNull(created);

        var getResponse = await client.GetAsync("/jobs");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var jobs = await getResponse.Content.ReadFromJsonAsync<IReadOnlyList<JobState>>();
        Assert.Contains(jobs!, j => j.Id == created!.Id);
    }

    [Fact]
    public async Task PostJobs_WithExplicitSeedAndSuffixWidth_PersistsThemOnTheJob()
    {
        // Seed/suffix width — явные входы задачи (спека), не только внутренние
        // детали Introspect
        var client = _app.CreateClient();
        var response = await client.PostAsJsonAsync("/jobs", new
        {
            source = new { dumpFilePath = "/tmp/test.dump" },
            seed = "explicit-seed-42",
            suffixWidth = 8
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await response.Content.ReadFromJsonAsync<JobState>();
        Assert.NotNull(created);
        Assert.Equal("explicit-seed-42", created!.Seed);
        Assert.Equal(8, created.SuffixWidth);
    }

    [Fact]
    public async Task PostJobs_LiveDbSource_RedactsPasswordInResponse()
    {
        var client = _app.CreateClient();
        var connectionString = "Host=db.internal;Port=5432;Database=prod;Username=admin;Password=super-secret";

        var response = await client.PostAsJsonAsync("/jobs", new
        {
            source = new { connectionString }
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("super-secret", body);
        var created = await response.Content.ReadFromJsonAsync<JobState>();
        Assert.NotNull(created);
        Assert.DoesNotContain("super-secret", created!.Source!.ConnectionString);
    }

    [Fact]
    public async Task GetJobs_LiveDbSource_RedactsPasswordInResponse()
    {
        var client = _app.CreateClient();
        var connectionString = "Host=db.internal;Port=5432;Database=prod;Username=admin;Password=super-secret";
        await client.PostAsJsonAsync("/jobs", new { source = new { connectionString } });

        var getResponse = await client.GetAsync("/jobs");
        var body = await getResponse.Content.ReadAsStringAsync();

        Assert.DoesNotContain("super-secret", body);
    }

    [Fact]
    public async Task GetJobById_LiveDbSource_RedactsPasswordInResponse()
    {
        var client = _app.CreateClient();
        var connectionString = "Host=db.internal;Port=5432;Database=prod;Username=admin;Password=super-secret";
        var postResponse = await client.PostAsJsonAsync("/jobs", new { source = new { connectionString } });
        var created = await postResponse.Content.ReadFromJsonAsync<JobState>();

        var getResponse = await client.GetAsync($"/jobs/{created!.Id}");
        var body = await getResponse.Content.ReadAsStringAsync();

        Assert.DoesNotContain("super-secret", body);
    }

    [Fact]
    public async Task GetJobById_ReturnsJob()
    {
        var client = _app.CreateClient();
        var postResponse = await client.PostAsJsonAsync("/jobs", new
        {
            source = new { dumpFilePath = "/tmp/test.dump" }
        });
        var created = await postResponse.Content.ReadFromJsonAsync<JobState>();
        Assert.NotNull(created);

        var getResponse = await client.GetAsync($"/jobs/{created!.Id}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var job = await getResponse.Content.ReadFromJsonAsync<JobState>();
        Assert.NotNull(job);
        Assert.Equal(created.Id, job.Id);
        Assert.Equal(JobStatus.Created, job.Status);
    }
}
