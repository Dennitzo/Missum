namespace Missum.App.Pages;

public sealed partial class SettingsPage
{
    /// <summary>Smoke access to the real Liquid Glass switch; setting it raises the normal Toggled handler.</summary>
    internal bool LiquidGlassSwitchIsOn
    {
        get => LiquidGlassToggle.IsOn;
        set => LiquidGlassToggle.IsOn = value;
    }

    internal void BringLiquidGlassSwitchIntoView() =>
        LiquidGlassToggle.StartBringIntoView(new Microsoft.UI.Xaml.BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0.3 });
}
