using System.Text.Json;
using Missum.App.Services;
using Missum.Core.Contracts;
using Missum.Core.Models;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private async Task VerifyScienceProjectCreationSmokeAsync()
    {
        var isolatedRoot = Environment.GetEnvironmentVariable("ASSISTANT_DATA_ROOT");
        if (!IsMessageFooterSmoke || string.IsNullOrWhiteSpace(isolatedRoot)
            || Environment.GetEnvironmentVariable("ASSISTANT_DISABLE_RUNTIME_AUTOSTART") != "1"
            || !string.Equals(Path.GetFullPath(isolatedRoot), Path.GetFullPath(App.Current.DataDirectory), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Der Projekttest benötigt ein isoliertes Profil ohne Modellstart.");
        var workspace = Path.Combine(isolatedRoot, "empty-science-project");
        Directory.CreateDirectory(workspace);
        if (Directory.EnumerateFileSystemEntries(workspace).Any())
            throw new InvalidOperationException("Der Science-Projekttest benötigt einen leeren Ordner.");
        Guid created = Guid.Empty;
        await _coordinator.CreateWorkspaceSessionAsync(workspace, ChatMode.ClaudeScience, (type, data, _) =>
        {
            if (type == "session.changed")
                created = JsonSerializer.SerializeToElement(data, JsonOptions).GetProperty("activeSessionId").GetGuid();
            return Task.CompletedTask;
        }, "science-project-smoke", _lifetime.Token);
        var chats = App.Current.GetService<IChatRepository>();
        var session = await chats.GetSessionAsync(created, _lifetime.Token);
        if (session is not { ChatMode: ChatMode.ClaudeScience } || session.CodingWorkspacePath != workspace)
            throw new InvalidOperationException("Die neue Science-Sitzung wurde nicht mit dem gewählten Projektordner gespeichert.");
        var package = ApplicationAssets.ResolvePath("Assets", "PublicationTool");
        foreach (var file in new[] { "Publikation-erzeugen.ps1", "PDF-ERZEUGEN.md",
                     ".publication-renderer/Missum.Publication.Cli.exe", ".publication-renderer/installation.json" })
            if (!File.Exists(Path.Combine(workspace, file)))
                throw new InvalidOperationException("Dem neuen Science-Projekt fehlt " + file);
        await File.WriteAllTextAsync(Path.Combine(isolatedRoot, "native-science-project-validation.json"),
            JsonSerializer.Serialize(new { passed = true, emptyProjectCreated = true, sessionSaved = true,
                publicationToolInstalled = true, package, workspace, modelRequests = 0 }), _lifetime.Token);
    }
}
