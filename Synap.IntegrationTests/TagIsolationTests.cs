using Synap.Domain;
using Synap.Infrastructure.Persistence.Data.Tags;
using Synap.Shared.Domain.ValueObjects.Ids;
using Xunit;

namespace Synap.IntegrationTests;

[Collection(PostgresCollection.Name)]
public class TagIsolationTests
{
    private readonly PostgresFixture _fixture;

    public TagIsolationTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task GetByNameAsync_never_returns_another_users_tag_with_the_same_name()
    {
        await using var context = _fixture.CreateContext();
        var tagWriteRepository = new TagWriteRepository(context);

        var userA = UserId.CreateFromDatabase(Guid.NewGuid());
        var userB = UserId.CreateFromDatabase(Guid.NewGuid());

        var userBsTag = Tag.Create(userB, "shared-name");
        await tagWriteRepository.CreateAsync(userBsTag, default);
        await context.SaveChangesAsync();

        var result = await tagWriteRepository.GetByNameAsync(userA.Value, "shared-name");

        Assert.Null(result);
    }

    [Fact]
    public async Task GetByNameAsync_finds_the_requesting_users_own_tag()
    {
        await using var context = _fixture.CreateContext();
        var tagWriteRepository = new TagWriteRepository(context);

        var userA = UserId.CreateFromDatabase(Guid.NewGuid());
        var tag = Tag.Create(userA, "own-tag");
        await tagWriteRepository.CreateAsync(tag, default);
        await context.SaveChangesAsync();

        var result = await tagWriteRepository.GetByNameAsync(userA.Value, "own-tag");

        Assert.NotNull(result);
        Assert.Equal(tag.Id.Value, result.Id.Value);
    }
}
