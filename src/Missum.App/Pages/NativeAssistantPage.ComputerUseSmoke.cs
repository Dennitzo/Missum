using Missum.Core.Contracts;
using Missum.Core.Models;
using Missum.App.Services;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    // Isolated UI fixture: never starts a model or injects desktop input.
    private async Task VerifyComputerUseSmokeAsync()
    {
        if (!IsMessageFooterSmoke || Environment.GetEnvironmentVariable("ASSISTANT_DISABLE_RUNTIME_AUTOSTART") != "1")
            throw new InvalidOperationException("Die Computersteuerungsprüfung benötigt ein isoliertes Profil.");
        var chats = App.Current.GetService<IChatRepository>();
        var fixture = await chats.CreateSessionAsync("Computersteuerung · Test", ChatMode.Coding, _lifetime.Token);
        if (!await CommandAsync("session.open", new { sessionId = fixture.Id }))
            throw new InvalidOperationException("Die Computersteuerungs-Test-Sitzung konnte nicht geöffnet werden.");
        ShowChatView(); RenderSessionTabs(); RenderMessagesNow(); UpdateLayout();
        _ = ShowFixtureAsync();

        async Task ShowFixtureAsync()
        {
            const string runId = "computer-ui-fixture";
            try
            {
                if (!await _computerApprovals.ActivateAsync(fixture.Id, runId, _lifetime.Token)) return;
                await _computerApprovals.RequestAsync(new ComputerApprovalRequest(fixture.Id, runId, "fixture-frame",
                    "click", "Testfenster", 120, 80, null), _lifetime.Token);
            }
            catch (OperationCanceledException) { }
            finally { _computerApprovals.EndRun(runId); }
        }
    }
}
