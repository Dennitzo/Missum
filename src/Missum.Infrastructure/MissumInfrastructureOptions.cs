namespace Missum.Infrastructure;

public sealed class MissumInfrastructureOptions
{
    public string DataDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Missum");

    public string DatabaseFileName { get; set; } = "Missum.db";
    public string SettingsFileName { get; set; } = "settings.json";
    public int LogCapacity { get; set; } = 10_000;
}
