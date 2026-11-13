using System.Text.Json;

namespace Missum.Core.Extensions;

public enum ExtensionChatMode
{
    General,
    Coding,
    ClaudeScience,
}

public enum ExtensionActionKind
{
    Immediate,
    SelectableTool,
}

public enum ExtensionSelectionBehavior
{
    None,
    Toggle,
}

public enum ExtensionEntrypointKind
{
    Desktop,
    Gateway,
}

public enum ExtensionToolRiskClass
{
    ReadOnly,
    LocalMutation,
    Process,
}

public enum ExtensionPermissionKind
{
    FileRead,
    FileWrite,
    Network,
    Process,
    WorkspaceRead,
    WorkspaceWrite,
}

/// <summary>
/// A purpose group in the composer tool menu. Extensions may declare their own groups so a
/// future integration can place itself beside related tools without a product-side change.
/// </summary>
public sealed record ExtensionGroupDescriptor(
    string GroupId,
    string Label,
    int GroupOrder);

public sealed record ExtensionActionDescriptor(
    string ActionId,
    string DisplayName,
    string Description,
    string IconKey,
    string GroupId,
    int GroupOrder,
    int ItemOrder,
    ExtensionChatMode[] SupportedChatModes,
    ExtensionActionKind ActionKind,
    ExtensionSelectionBehavior SelectionBehavior,
    string? DisabledReason = null);

public sealed record ExtensionToolDescriptor(
    string ActionId,
    string ModelToolName,
    string Description,
    JsonElement InputSchema,
    int TimeoutSeconds,
    int MaximumOutputBytes,
    ExtensionToolRiskClass RiskClass = ExtensionToolRiskClass.ReadOnly);

public sealed record ExtensionEntrypointDescriptor(
    ExtensionEntrypointKind Kind,
    string Path);

public sealed record ExtensionManifest(
    int SchemaVersion,
    string Id,
    string Version,
    string DisplayName,
    string Publisher,
    ExtensionEntrypointDescriptor[] Entrypoints,
    ExtensionPermissionKind[] Permissions,
    ExtensionActionDescriptor[] Actions,
    ExtensionToolDescriptor[] Tools,
    // Optional: labels for the groups this package's actions use. Unknown groups keep their raw id.
    // Packages from before this field stays valid because it defaults to no declarations.
    ExtensionGroupDescriptor[]? Groups = null);

public sealed record ExtensionValidationResult(IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;

    public void ThrowIfInvalid()
    {
        if (!IsValid)
            throw new InvalidDataException("Das Extension-Manifest ist ungültig: " + string.Join(" ", Errors));
    }
}
