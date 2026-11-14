using System.IO.Abstractions;
using System.Text.Json;
using OpenCode.Agents.AI.Tools.Infrastructure.Processes.Abstractions;

namespace OpenCode.Agents.AI.Tools.Clones;

/// <summary>
/// Runs the pinned jscpd over one clone scope: it writes the scope's jscpd config into a work
/// folder, runs the detector through Bun from the repository root, and reads the totals of
/// the JSON report it writes. A gated scope runs against its baseline and fails on a new clone or
/// stale baseline entry or above its threshold; with an update it rewrites the baseline instead. Every scope fails when it
/// analyses no file. jscpd's own defaults stay: it honours .gitignore, follows no symbolic link, and
/// skips files above 1 MiB.
/// </summary>
internal sealed class CloneDetector(IFileSystem fileSystem, IProcessRunner processRunner)
{
    internal const string RunnerPath = CloneScopes.DetectorFolder + "/node_modules/jscpd/run-jscpd.js";
    internal const string ReportFileName = "jscpd-report.json";
    internal const string SarifReportFileName = "jscpd-report.sarif";

    /// <summary>jscpd otherwise writes an empty report and exits 0 when nothing was analysed, which would read as a clean scope.</summary>
    private const string FailOnEmpty = "--fail-on-empty";
    private const int MinimumTokens = 50;
    private static readonly string[] CommonIgnore = ["**/bin/**", "**/obj/**", "**/node_modules/**"];

    private readonly IFileSystem _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    private readonly IProcessRunner _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));

    /// <summary>Refuses unless the pinned detector is installed under the repository root.</summary>
    public void EnsureInstalled(string root)
    {
        if (!_fileSystem.File.Exists(_fileSystem.Path.Combine(root, RunnerPath)))
        {
            throw new CloneDetectorException(
                "The clone detector is not installed. Run once, with the machine's Bun: "
                + "cd " + CloneScopes.DetectorFolder + " && bun install --frozen-lockfile --ignore-scripts");
        }
    }

    /// <summary>Runs one scope from the repository root, with its config and report in <paramref name="workDirectory"/>.</summary>
    public async Task<CloneScopeResult> RunAsync(
        string root, CloneScope scope, string workDirectory, bool updateBaseline, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var output = _fileSystem.Path.Combine(workDirectory, scope.Name);
        var config = _fileSystem.Path.Combine(workDirectory, scope.Name + ".json");
        await WriteConfigAsync(config, output, Ignore(scope), cancellationToken).ConfigureAwait(false);

        var result = await _processRunner.RunAsync("bun", Arguments(scope, config, updateBaseline), root, cancellationToken).ConfigureAwait(false);
        var report = _fileSystem.Path.Combine(output, ReportFileName);
        if (!_fileSystem.File.Exists(report))
        {
            throw new CloneDetectorException(string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"jscpd wrote no report for scope '{scope.Name}' (exit {result.ExitCode}): {result.StandardError.Trim()}"));
        }

        using var document = JsonDocument.Parse(await _fileSystem.File.ReadAllTextAsync(report, cancellationToken).ConfigureAwait(false));
        var total = document.RootElement.GetProperty("statistics").GetProperty("total");
        return new CloneScopeResult(
            scope,
            result.ExitCode,
            total.GetProperty("clones").GetInt32(),
            total.GetProperty("duplicatedLines").GetInt32(),
            total.GetProperty("lines").GetInt32(),
            total.GetProperty("percentage").GetDouble(),
            total.TryGetProperty("newClones", out var newClones) ? newClones.GetInt32() : 0,
            NewCloneLocations(scope, document.RootElement),
            scope.Baseline is not null && !updateBaseline
                ? await StaleFingerprintsAsync(root, scope.Baseline, output, cancellationToken).ConfigureAwait(false)
                : [],
            result.StandardError.Trim());
    }

    /// <summary>The path of the report a run of <paramref name="scope"/> writes into <paramref name="workDirectory"/>.</summary>
    public string ReportPath(string workDirectory, CloneScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        return _fileSystem.Path.Combine(workDirectory, scope.Name, ReportFileName);
    }

    /// <summary>The path of the SARIF report a run of <paramref name="scope"/> writes into <paramref name="workDirectory"/>.</summary>
    public string SarifReportPath(string workDirectory, CloneScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        return _fileSystem.Path.Combine(workDirectory, scope.Name, SarifReportFileName);
    }

    /// <summary>Uses the detector's own content fingerprints from the same scan, rather than reproducing its hash algorithm.</summary>
    private async Task<IReadOnlyList<string>> StaleFingerprintsAsync(string root, string baseline, string output, CancellationToken cancellationToken)
    {
        using var accepted = JsonDocument.Parse(await _fileSystem.File.ReadAllTextAsync(_fileSystem.Path.Combine(root, baseline), cancellationToken).ConfigureAwait(false));
        using var reported = JsonDocument.Parse(await _fileSystem.File.ReadAllTextAsync(_fileSystem.Path.Combine(output, SarifReportFileName), cancellationToken).ConfigureAwait(false));
        var current = reported.RootElement.GetProperty("runs").EnumerateArray()
            .SelectMany(static run => run.GetProperty("results").EnumerateArray())
            .Select(static result => result.GetProperty("partialFingerprints").GetProperty("jscpdCloneHash/v1").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        return [.. accepted.RootElement.GetProperty("fingerprints").EnumerateObject()
            .Select(static entry => entry.Name).Except(current, StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Where each clone the baseline does not hold sits. jscpd marks those duplicates <c>isNew</c> and
    /// names their files relative to the scanned path, so the scope's path is put in front.
    /// </summary>
    private static List<CloneLocation> NewCloneLocations(CloneScope scope, JsonElement report) =>
        !report.TryGetProperty("duplicates", out var duplicates)
            ? []
            :
            [
                .. duplicates.EnumerateArray()
                    .Where(static duplicate => duplicate.TryGetProperty("isNew", out var isNew) && isNew.ValueKind == JsonValueKind.True)
                    .Select(duplicate => new CloneLocation(
                        Fragment(scope, duplicate.GetProperty("firstFile")),
                        Fragment(scope, duplicate.GetProperty("secondFile")),
                        duplicate.GetProperty("tokens").GetInt32())),
            ];

    private static string Fragment(CloneScope scope, JsonElement file) =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{scope.Path}/{file.GetProperty("name").GetString()!.Replace('\\', '/')}:{file.GetProperty("start").GetInt32()}-{file.GetProperty("end").GetInt32()}");

    internal static IReadOnlyList<string> Arguments(CloneScope scope, string config, bool updateBaseline)
    {
        ArgumentNullException.ThrowIfNull(scope);

        List<string> arguments = [RunnerPath, scope.Path, "--config", config, FailOnEmpty];
        if (scope.Baseline is null)
        {
            return arguments;
        }

        arguments.AddRange(["--baseline", scope.Baseline]);
        if (updateBaseline)
        {
            arguments.Add("--update-baseline");
            return arguments;
        }

        arguments.Add("--fail-on-new-clones");
        if (scope.Threshold is { } threshold)
        {
            arguments.AddRange(["--threshold", threshold.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        }

        return arguments;
    }

    private static IReadOnlyList<string> Ignore(CloneScope scope) =>
    [
        .. CommonIgnore,
        .. scope.Ignore,
    ];

    private async Task WriteConfigAsync(string config, string output, IReadOnlyList<string> ignore, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true });
        await using (writer.ConfigureAwait(false))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("ignore");
            foreach (var glob in ignore)
            {
                writer.WriteStringValue(glob);
            }

            writer.WriteEndArray();
            writer.WriteStartArray("ignorePattern");
            writer.WriteStringValue(@"(?m)^[ \t]*(global[ \t]+)?using[ \t]+(static[ \t]+)?([A-Za-z_][A-Za-z0-9_]*[ \t]*=[ \t]*)?[A-Za-z_][A-Za-z0-9_.]*[ \t]*;");
            writer.WriteStringValue(@"(?m)^[ \t]*_[A-Za-z_][A-Za-z0-9_]*[ \t]*=[ \t]*[A-Za-z_][A-Za-z0-9_]*[ \t]*\?\?[ \t]*throw[ \t]+new[ \t]+ArgumentNullException\(nameof\([A-Za-z_][A-Za-z0-9_]*\)\);[ \t]*$");
            writer.WriteStringValue(@"(?m)^[ \t]*ArgumentNullException\.ThrowIfNull\([A-Za-z_][A-Za-z0-9_]*\);[ \t]*$");
            writer.WriteStringValue(@"(?m)^[ \t]*ArgumentException\.ThrowIfNullOrWhiteSpace\([A-Za-z_][A-Za-z0-9_]*\);[ \t]*$");
            writer.WriteEndArray();
            writer.WriteStartArray("format");
            writer.WriteStringValue("csharp");
            writer.WriteEndArray();
            writer.WriteNumber("minTokens", MinimumTokens);
            writer.WriteStartArray("reporters");
            writer.WriteStringValue("console");
            writer.WriteStringValue("json");
            writer.WriteStringValue("sarif");
            writer.WriteEndArray();
            writer.WriteString("output", output);
            writer.WriteEndObject();
        }

        await _fileSystem.File.WriteAllBytesAsync(config, buffer.ToArray(), cancellationToken).ConfigureAwait(false);
    }
}
