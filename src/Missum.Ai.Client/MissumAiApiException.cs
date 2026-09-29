using Missum.Ai.Contracts;

namespace Missum.Ai.Client;

public sealed class MissumAiApiException : HttpRequestException
{
    public MissumAiApiException(string message, int statusCode, MissumAiProblem? problem = null)
        : base(message, null, (System.Net.HttpStatusCode)statusCode)
    {
        Problem = problem;
    }

    public MissumAiProblem? Problem { get; }
}
