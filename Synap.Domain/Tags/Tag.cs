using SergioIzq.Domain.Kernel.Abstractions;
using Synap.Shared.Domain.ValueObjects.Ids;
using System.ComponentModel.DataAnnotations.Schema;

namespace Synap.Domain;

[Table("tags")]
public sealed class Tag : AbsEntity<TagId>
{
    private Tag() : base(TagId.Create(Guid.NewGuid()).Value)
    {
    }

    private Tag(TagId id, UserId userId, string name) : base(id)
    {
        UserId = userId;
        Name = name;
    }

    public UserId UserId { get; private set; }
    public string Name { get; private set; } = null!;

    public static Tag Create(UserId userId, string name) => new(TagId.Create(Guid.NewGuid()).Value, userId, name);
}
