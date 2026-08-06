// Strategies/PoolMaterialsRepositoryTests.cs
using DbAnonymizer.Core.Strategies;
using Testcontainers.PostgreSql;

namespace DbAnonymizer.Core.Tests.Strategies;

public sealed class PoolMaterialsRepositoryTests : IAsyncLifetime
{
    private string _connectionString = null!;
    private PostgreSqlContainer _container = null!;

    public async Task InitializeAsync()
    {
        _container = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await _container.StartAsync();
        _connectionString = _container.GetConnectionString();
    }

    [Fact]
    public async Task TryLoad_NothingSaved_ReturnsNull()
    {
        var repository = new PoolMaterialsRepository(_connectionString);

        var loaded = await repository.TryLoadAsync("plan-a", "firstname");

        Assert.Null(loaded);
    }

    [Fact]
    public async Task Save_Then_TryLoad_Roundtrip()
    {
        var repository = new PoolMaterialsRepository(_connectionString);
        var values = new[] { "Ivan", "Petr", "Sergey" };

        await repository.SaveAsync("plan-a", "firstname", values);
        var loaded = await repository.TryLoadAsync("plan-a", "firstname");

        Assert.Equal(values, loaded);
    }

    [Fact]
    public async Task TryLoad_DifferentComponentSamePlan_ReturnsNull()
    {
        var repository = new PoolMaterialsRepository(_connectionString);
        await repository.SaveAsync("plan-a", "firstname", new[] { "Ivan" });

        var loaded = await repository.TryLoadAsync("plan-a", "lastname");

        Assert.Null(loaded);
    }

    [Fact]
    public async Task TryLoad_SameComponentDifferentPlan_ReturnsNull()
    {
        var repository = new PoolMaterialsRepository(_connectionString);
        await repository.SaveAsync("plan-a", "firstname", new[] { "Ivan" });

        var loaded = await repository.TryLoadAsync("plan-b", "firstname");

        Assert.Null(loaded);
    }

    [Fact]
    public async Task Save_Twice_OverwritesPreviousValues()
    {
        var repository = new PoolMaterialsRepository(_connectionString);
        await repository.SaveAsync("plan-a", "firstname", new[] { "Ivan" });

        await repository.SaveAsync("plan-a", "firstname", new[] { "Petr", "Sergey" });
        var loaded = await repository.TryLoadAsync("plan-a", "firstname");

        Assert.Equal(new[] { "Petr", "Sergey" }, loaded);
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }
}
