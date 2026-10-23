using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Missum.Ai.Contracts;
using Missum.Core.Coding;

namespace Missum.App.Services;

public sealed partial class LocalToolBroker
{
    private static async Task<(JsonElement Result, string? FailureMessage)> ExecuteCodingWithRepositoryFilesAsync(
        ToolProposal proposal,
        string workspacePath,
        Func<CodingCommandProgress, Task>? commandProgress,
        CodingRunEvidenceStore.CodingEvidenceCapture? evidence,
        CancellationToken token)
    {
        object? preflightMaintenance = null;
        var maintenanceWorkspace = workspacePath;
        var gitPublishing = proposal.Name == ClientToolNames.CodingCommand ? ReadGitPublishingCommand(proposal.Arguments, workspacePath) : null;
        if (gitPublishing is not null)
        {
            try
            {
                maintenanceWorkspace = await ResolveGitPublishingRootAsync(gitPublishing, workspacePath, token).ConfigureAwait(false);
                var maintained = await ProjectRepositoryScaffold.EnsureAsync(maintenanceWorkspace, cancellationToken: token).ConfigureAwait(false);
                if (maintained.Changed) preflightMaintenance = RepositoryMaintenanceReceipt(maintained, workspacePath: maintenanceWorkspace);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Nothing was staged/published yet. A broken ignore file must not let
                // a Git publication step accidentally include the local PDF runtime.
                var blocked = JsonSerializer.SerializeToElement(new
                {
                    success = false, executed = false,
                    reason = "Git wurde vor der Ausführung angehalten, weil das Git-Arbeitsverzeichnis oder dessen lokale Ausschlussregeln nicht sicher vorbereitet werden konnten.",
                    projectRepositoryFiles = new { success = false,
                        warning = new { code = "project.repository_files_preflight_failed", message = exception.Message } },
                }, JsonOptions);
                return (blocked, "Git wurde nicht ausgeführt: Zielverzeichnis beziehungsweise .gitignore zuerst prüfen. " + exception.Message);
            }
        }
        using var observation = proposal.Name == ClientToolNames.CodingCommand ? new RepositoryMutationObservation(workspacePath) : null;
        JsonElement result;
        string? failure = null;
        try
        {
            result = await new LocalCodingToolExecutor(workspacePath, commandProgress, evidence)
                .ExecuteAsync(proposal.Name, proposal.Arguments, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A command can fail after writing files. Keep its original failure while
            // still maintaining repository metadata for changes actually observed.
            failure = exception.Message;
            result = JsonSerializer.SerializeToElement(new { failed = true }, JsonOptions);
        }

        if (preflightMaintenance is not null) result = WithRepositoryMaintenance(result, preflightMaintenance);
        if (observation is not null) await observation.CompleteAsync().ConfigureAwait(false);
        var success = failure is null && (!result.TryGetProperty("success", out var succeeded) || succeeded.ValueKind != JsonValueKind.False);
        var directMutation = success && (proposal.Name is ClientToolNames.CodingWrite or ClientToolNames.CodingEdit
            ? result.TryGetProperty("applied", out var applied) && applied.ValueKind == JsonValueKind.True
            : proposal.Name == ClientToolNames.CodingUndo);
        if (!directMutation && observation?.MutationObserved != true)
        {
            if (observation?.Warning is { } observerWarning)
                result = WithRepositoryMaintenance(result, new
                {
                    success = false,
                    warning = new { code = "project.repository_files_observation_failed", message = observerWarning },
                });
            return (result, failure);
        }

        try
        {
            var maintained = await ProjectRepositoryScaffold.EnsureAsync(maintenanceWorkspace, cancellationToken: token).ConfigureAwait(false);
            if (maintained.Changed || observation?.Warning is not null)
                result = WithRepositoryMaintenance(result, RepositoryMaintenanceReceipt(maintained, observation?.Warning, maintenanceWorkspace));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The primary mutation already happened. Retrying it because optional
            // metadata maintenance failed could duplicate changes or corrupt anchors.
            result = WithRepositoryMaintenance(result, new
            {
                success = false,
                warning = new { code = "project.repository_files_maintenance_failed", message = exception.Message },
            });
        }
        return (result, failure);
    }

    private static object RepositoryMaintenanceReceipt(ProjectRepositoryScaffoldResult maintained, string? observationWarning = null, string? workspacePath = null) => new
    {
        success = true,
        maintained.ReadmeCreated,
        maintained.GitIgnoreUpdated,
        maintained.DetectedTechnologies,
        readmeNeedsContent = maintained.ReadmeCreated,
        readmeStatus = maintained.ReadmeCreated ? "template_needs_project_content" : "existing_preserved",
        nextAction = maintained.ReadmeCreated
            ? "README.md ist nur eine Startvorlage. Ergänze jetzt tatsächlichen Projektzweck, Voraussetzungen, überprüfte Verwendung und relevante Struktur; nicht als fertige Dokumentation behandeln."
            : null,
        readmePath = "README.md",
        gitIgnorePath = ".gitignore",
        workspacePath,
        observationWarning,
    };

    private sealed record GitPublishingCommand(string Executable, string[] GlobalArguments, string WorkingDirectory, string? Error = null);

    private static GitPublishingCommand? ReadGitPublishingCommand(JsonElement arguments, string workspacePath)
    {
        if (!arguments.TryGetProperty("executable", out var executableValue) || executableValue.ValueKind != JsonValueKind.String
            || !arguments.TryGetProperty("arguments", out var values) || values.ValueKind != JsonValueKind.Array
            || values.GetArrayLength() > 64 || values.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String || item.GetString()!.Length > 4000)) return null;
        var executable = Path.GetFileNameWithoutExtension(executableValue.GetString())?.ToLowerInvariant();
        var args = values.EnumerateArray().Select(item => item.GetString()!).ToArray();
        var cwd = arguments.TryGetProperty("workingDirectory", out var working) && working.ValueKind == JsonValueKind.String ? working.GetString()! : ".";
        if (cwd.Length > 1024 || cwd.Any(char.IsControl)) return null;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspacePath));
        var directory = Path.GetFullPath(cwd, root);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if ((!directory.Equals(root, comparison) && !directory.StartsWith(root + Path.DirectorySeparatorChar, comparison)) || !Directory.Exists(directory)) return null;

        if (string.Equals(executable, "git", StringComparison.OrdinalIgnoreCase))
            return ReadGitArguments(executableValue.GetString()!, args, directory);
        if (executable is not ("powershell" or "pwsh" or "cmd" or "bash" or "sh")) return null;
        for (var index = 0; index + 1 < args.Length; index++)
        {
            if (!args[index].Equals("-Command", StringComparison.OrdinalIgnoreCase)
                && !args[index].Equals("-c", StringComparison.OrdinalIgnoreCase)
                && !args[index].Equals("/c", StringComparison.OrdinalIgnoreCase)) continue;
            var script = args[index + 1];
            if (executable is "powershell" or "pwsh" or "cmd" && TryReadSimpleShellCommand(script, executable == "cmd", out var tokens)
                && index + 2 == args.Length && tokens.Length > 0
                && string.Equals(Path.GetFileNameWithoutExtension(tokens[0]), "git", StringComparison.OrdinalIgnoreCase))
                return ReadGitArguments(tokens[0], tokens[1..], directory);
            if (Regex.IsMatch(script, "\\bgit(?:\\.exe)?[\\s\"'](?:(?![;\\r\\n|&]).)*\\b(?:add|stage|commit|push)\\b",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)))
                return new("git", [], directory, "Der Git-Veröffentlichungsbefehl enthält mehrdeutige Shell-Syntax. Verwende coding.command mit executable=git und einem Argumente-Array.");
            return null;
        }
        return null;
    }

    private static GitPublishingCommand? ReadGitArguments(string executable, string[] args, string directory)
    {
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (argument is "-C" or "-c" or "--git-dir" or "--work-tree" or "--namespace" or "--config-env") { index++; continue; }
            if (argument.StartsWith("-C", StringComparison.Ordinal) || argument.StartsWith("-c", StringComparison.Ordinal)
                || argument.StartsWith("--git-dir=", StringComparison.Ordinal) || argument.StartsWith("--work-tree=", StringComparison.Ordinal)
                || argument.StartsWith("--namespace=", StringComparison.Ordinal) || argument.StartsWith("--config-env=", StringComparison.Ordinal)
                || argument is "--no-pager" or "--paginate" or "--no-replace-objects" or "--literal-pathspecs" or "--no-literal-pathspecs"
                    or "--glob-pathspecs" or "--noglob-pathspecs" or "--icase-pathspecs" or "--no-optional-locks" or "--optional-locks" or "--bare") continue;
            if (argument.StartsWith('-'))
                return args.Any(value => value is "add" or "stage" or "commit" or "push")
                    ? new(executable, [], directory, "Nicht unterstützte globale Git-Option; das Ziel kann vor dem Veröffentlichen nicht sicher bestimmt werden.") : null;
            return argument is "add" or "stage" or "commit" or "push" ? new(executable, args[..index], directory) : null;
        }
        return null;
    }

    private static bool TryReadSimpleShellCommand(string script, bool cmd, out string[] tokens)
    {
        var parts = new List<string>();
        var part = new StringBuilder();
        var quote = '\0';
        var started = false;
        script = script.Trim();
        if (!cmd && script.StartsWith('&')) script = script[1..].TrimStart();
        foreach (var character in script)
        {
            if (character is '$' or '%' or '!' or '@' or '`' or '\r' or '\n' or ';' or '|' or '&' or '>' or '<') { tokens = []; return false; }
            if (quote != '\0')
            {
                if (character == quote) quote = '\0';
                else part.Append(character);
                continue;
            }
            if (character == '"' || (!cmd && character == '\'')) { quote = character; started = true; continue; }
            if (character == '#') { tokens = []; return false; }
            if (char.IsWhiteSpace(character))
            {
                if (started) { parts.Add(part.ToString()); part.Clear(); started = false; }
            }
            else { part.Append(character); started = true; }
        }
        if (quote != '\0') { tokens = []; return false; }
        if (started) parts.Add(part.ToString());
        tokens = parts.ToArray();
        return true;
    }

    private static async Task<string> ResolveGitPublishingRootAsync(GitPublishingCommand command, string workspacePath, CancellationToken token)
    {
        if (command.Error is not null) throw new InvalidDataException(command.Error);
        for (var parent = new DirectoryInfo(command.WorkingDirectory); parent is not null; parent = parent.Parent)
            if ((parent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Verknüpfte Git-Arbeitsverzeichnisse werden nicht automatisch verändert.");
        var executable = !Path.IsPathFullyQualified(command.Executable)
            && (command.Executable.Contains(Path.DirectorySeparatorChar) || command.Executable.Contains(Path.AltDirectorySeparatorChar))
            ? Path.GetFullPath(command.Executable, command.WorkingDirectory) : command.Executable;
        using var process = new Process { StartInfo = new(executable)
        {
            WorkingDirectory = command.WorkingDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        } };
        foreach (var argument in command.GlobalArguments) process.StartInfo.ArgumentList.Add(argument);
        process.StartInfo.ArgumentList.Add("--no-optional-locks");
        process.StartInfo.ArgumentList.Add("rev-parse");
        process.StartInfo.ArgumentList.Add("--show-toplevel");
        process.StartInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        process.StartInfo.Environment["CI"] = "true";
        process.StartInfo.Environment["NO_COLOR"] = "1";
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync(token);
        var error = process.StandardError.ReadToEndAsync(token);
        try { await process.WaitForExitAsync(token).ConfigureAwait(false); }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
        var path = (await output.ConfigureAwait(false)).Trim();
        if (process.ExitCode != 0 || path.Length == 0 || path.Contains('\n') || path.Contains('\r') || !Path.IsPathFullyQualified(path))
            throw new IOException("Das Git-Arbeitsverzeichnis konnte nicht eindeutig ermittelt werden: " + await error.ConfigureAwait(false));
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspacePath));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!target.Equals(root, comparison) && !target.StartsWith(root + Path.DirectorySeparatorChar, comparison))
            throw new UnauthorizedAccessException("Das Git-Arbeitsverzeichnis liegt außerhalb des ausgewählten Projekts: " + target);
        return target;
    }

    private static JsonElement WithRepositoryMaintenance(JsonElement result, object maintenance)
    {
        var enriched = JsonNode.Parse(result.GetRawText())!.AsObject();
        enriched["projectRepositoryFiles"] = JsonSerializer.SerializeToNode(maintenance, JsonOptions);
        return JsonSerializer.SerializeToElement(enriched, JsonOptions);
    }

    /// <summary>Constant-memory observation limited to the lifetime of one command; no tree scans or persistent watchers.</summary>
    private sealed class RepositoryMutationObservation : IDisposable
    {
        private readonly string _root;
        private readonly string _prefix;
        private readonly StringComparison _comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        private readonly TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private FileSystemWatcher? _watcher;
        private int _mutationObserved;
        private string? _warning;

        internal RepositoryMutationObservation(string workspacePath)
        {
            _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspacePath));
            _prefix = Path.EndsInDirectorySeparator(_root) ? _root : _root + Path.DirectorySeparatorChar;
            try
            {
                for (var parent = new DirectoryInfo(_root); parent is not null; parent = parent.Parent)
                    if ((parent.Attributes & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Verknüpfte Projektpfade werden nicht automatisch beobachtet.");
                _watcher = new(_root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = 16 * 1024,
                };
                _watcher.Changed += OnChanged;
                _watcher.Created += OnChanged;
                _watcher.Deleted += OnChanged;
                _watcher.Renamed += (_, change) => { Observe(change.OldFullPath); Observe(change.FullPath); };
                _watcher.Error += (_, error) => SetWarning(error.GetException().Message);
                _watcher.EnableRaisingEvents = true;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                SetWarning(exception.Message);
                _watcher?.Dispose();
                _watcher = null;
            }
        }

        internal bool MutationObserved => Volatile.Read(ref _mutationObserved) != 0;
        internal string? Warning => Volatile.Read(ref _warning);

        private void OnChanged(object sender, FileSystemEventArgs change) => Observe(change.FullPath);

        private void Observe(string fullPath)
        {
            if (MutationObserved || !fullPath.StartsWith(_prefix, _comparison)) return;
            var relative = fullPath[_prefix.Length..];
            var parts = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Any(part => part.Equals(".git", StringComparison.OrdinalIgnoreCase))) return;
            try
            {
                var path = _root;
                foreach (var part in parts)
                {
                    path = Path.Combine(path, part);
                    try
                    {
                        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return;
                    }
                    catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) { }
                }
                Interlocked.Exchange(ref _mutationObserved, 1);
                _changed.TrySetResult();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException) { SetWarning(exception.Message); }
        }

        private void SetWarning(string message) => Interlocked.CompareExchange(ref _warning, message[..Math.Min(message.Length, 1000)], null);

        internal async Task CompleteAsync()
        {
            // Let a final native notification reach the callback before detaching.
            // A no-change command pays at most one short bounded wait, never a scan.
            if (_watcher is not null && !MutationObserved)
                await Task.WhenAny(_changed.Task, Task.Delay(50)).ConfigureAwait(false);
            Dispose();
        }

        public void Dispose()
        {
            _watcher?.Dispose();
            _watcher = null;
        }
    }
}
