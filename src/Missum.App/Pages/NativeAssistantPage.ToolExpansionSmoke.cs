using System.Text.Json;
namespace Missum.App.Pages;
public sealed partial class NativeAssistantPage
{
    private Task VerifyToolExpansionDefaultsSmokeAsync(JsonElement original) => VerifyToolTabSmokeAsync(original);
}
