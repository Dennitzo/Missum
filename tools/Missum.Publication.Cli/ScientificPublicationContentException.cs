namespace Missum.App.Services;

// The shared image preparation requires only its content-error classification.
// The WinUI service's richer database repair metadata is not needed by this CLI.
internal sealed class ScientificPublicationContentException(string message) : Exception(message);
