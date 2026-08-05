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
            dumpFilePath = "/tmp/test.dump"
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task GetJobs_ReturnsCreatedJob()
    {
        var client = _app.CreateClient();
        var postResponse = await client.PostAsJsonAsync("/jobs", new
        {
            dumpFilePath = "/tmp/test.dump"
        });
        var created = await postResponse.Content.ReadFromJsonAsync<JobState>();
        Assert.NotNull(created);

        var getResponse = await client.GetAsync("/jobs");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var jobs = await getResponse.Content.ReadFromJsonAsync<IReadOnlyList<JobState>>();
        Assert.Contains(jobs!, j => j.Id == created!.Id);
    }

    [Fact]
    public async Task GetJobById_ReturnsJob()
    {
        var client = _app.CreateClient();
        var postResponse = await client.PostAsJsonAsync("/jobs", new
        {
            dumpFilePath = "/tmp/test.dump"
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
