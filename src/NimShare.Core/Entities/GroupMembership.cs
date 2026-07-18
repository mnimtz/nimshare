namespace NimShare.Core.Entities;

public enum GroupRole
{
    Member = 0,

    /// <summary>Manages the group: add/remove members, upload/delete any group file, delete the group.</summary>
    Manager = 1
}

public class GroupMembership
{
    public Guid GroupId { get; set; }
    public Group Group { get; set; } = null!;

    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    public GroupRole Role { get; set; } = GroupRole.Member;

    public DateTimeOffset JoinedAt { get; set; } = DateTimeOffset.UtcNow;
}
