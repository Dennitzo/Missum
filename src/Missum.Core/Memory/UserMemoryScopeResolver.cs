namespace Missum.Core.Memory;

/// <summary>
/// Derives the project identity of a chat from Missums own session assignment, in exactly the
/// order the project memory already uses: the workspace the session was opened with, then the
/// workspace of its session group, then the group id itself. Keeping one implementation means a
/// personal project rule is found in every chat of the same project and in no other.
/// </summary>
public static class UserMemoryScopeResolver
{
    public static UserMemoryScopeContext Resolve(
        string? sessionWorkspacePath,
        string? groupWorkspacePath,
        string? groupId,
        string? groupLabel = null)
    {
        if (!string.IsNullOrWhiteSpace(sessionWorkspacePath))
        {
            var scope = ProjectMemoryScope.FromWorkspace(sessionWorkspacePath, groupId);
            return new UserMemoryScopeContext(scope.ProjectKey, scope.ProjectId, Label(scope.ProjectKey, groupLabel));
        }

        if (string.IsNullOrWhiteSpace(groupId)) return UserMemoryScopeContext.Global;

        if (!string.IsNullOrWhiteSpace(groupWorkspacePath))
        {
            var scope = ProjectMemoryScope.FromWorkspace(groupWorkspacePath, groupId);
            return new UserMemoryScopeContext(scope.ProjectKey, groupId, groupLabel ?? Label(scope.ProjectKey, null));
        }

        var project = ProjectMemoryScope.FromProjectId(groupId);
        return new UserMemoryScopeContext(project.ProjectKey, groupId, groupLabel);
    }

    private static string? Label(string projectKey, string? preferred)
    {
        if (!string.IsNullOrWhiteSpace(preferred)) return preferred;
        var separator = projectKey.LastIndexOfAny(['\\', '/']);
        var name = separator >= 0 ? projectKey[(separator + 1)..] : projectKey;
        name = name.Replace("workspace:", string.Empty, StringComparison.OrdinalIgnoreCase);
        return string.IsNullOrWhiteSpace(name) ? projectKey : name;
    }
}
