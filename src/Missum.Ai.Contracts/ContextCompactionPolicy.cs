namespace Missum.Ai.Contracts;

/// <summary>Shared working-context boundary; never reduces the model's allocated window.</summary>
public static class ContextCompactionPolicy
{
    public const int TriggerPercent = 80;

    public static int InputTokenBudget(int contextLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(contextLength, 2048);
        var safety = Math.Min(8192, Math.Max(2048, contextLength / 16));
        return Math.Max(1024, (int)Math.Min((long)contextLength * TriggerPercent / 100, (long)contextLength - safety));
    }
}
