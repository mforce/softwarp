using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Softwarp.Core;

namespace Softwarp.Cli;

public abstract record Command
{
    private Command() { }
    public sealed record Start(ItemId Item) : Command;
    /// <summary>Wait bounds how long one call may block on a running agent.</summary>
    public sealed record Advance(RunId Run, TimeSpan? Wait) : Command;
    public sealed record Status(RunId Run) : Command;
    public sealed record Spec(RunId Run) : Command;
    public sealed record Help : Command;
}

public sealed record Invocation(Command Command, bool Json);

/// <summary>Arguments, output and exit codes. Verbs are parsed explicitly; nothing is loaded by reflection, and no flag
/// skips or forces a step.</summary>
public static class Cli
{
    /// <summary>Exits other than <see cref="ExitStatus"/>: the call failed in a way the factory did not decide.</summary>
    public const int Unexpected = 1;

    public const string Usage = """
        usage: softwarp start <owner>/<repo> <issue> [--json]
               softwarp advance <run> [--wait <n>s|<n>m|<n>h] [--json]
               softwarp status <run> [--json]
               softwarp spec <run> [--json]
        exit:  0 ok, 2 invalid input, 3 a gate failed, 4 waiting on an agent, 5 waiting on the maintainer,
               6 another caller is busy with the item, 1 unexpected error
        """;

    /// <summary>Null when the arguments name no verb this CLI has.</summary>
    public static Invocation? Parse(string[] args)
    {
        var json = args.Contains("--json");
        Command? command = args.Where(arg => arg != "--json").ToArray() switch
        {
            ["start", var repository, var issue] when repository.Split('/') is [var owner, var repo]
                && long.TryParse(issue, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0 =>
                new Command.Start(new ItemId(owner, repo, number)),
            ["advance", var run] => new Command.Advance(new RunId(run), null),
            ["advance", var run, "--wait", var wait] when Duration(wait) is { } duration => new Command.Advance(new RunId(run), duration),
            ["status", var run] => new Command.Status(new RunId(run)),
            ["spec", var run] => new Command.Spec(new RunId(run)),
            ["help" or "--help" or "-h"] => new Command.Help(),
            _ => null,
        };
        return command is null ? null : new Invocation(command, json);
    }

    /// <summary>Parses the arguments, then composes the factory, then runs the one verb. The exit code is the factory's
    /// <see cref="ExitStatus"/>, 2 for arguments or a machine config it cannot use, or <see cref="Unexpected"/>.</summary>
    public static async Task<int> RunAsync(string[] args, Func<Factory> compose, TextWriter stdout, TextWriter stderr,
        CancellationToken ct)
    {
        if (Parse(args) is not { } invocation)
        {
            await stderr.WriteLineAsync(Usage);
            return (int)ExitStatus.InvalidInput;
        }
        if (invocation.Command is Command.Help)
        {
            await stdout.WriteLineAsync(Usage);
            return (int)ExitStatus.Ok;
        }
        Factory factory;
        try
        {
            factory = compose();
        }
        catch (InvalidOperationException e)
        {
            await stderr.WriteLineAsync($"softwarp: {e.Message}");
            return (int)ExitStatus.InvalidInput;
        }
        try
        {
            if (invocation.Command is Command.Spec spec) return await SpecAsync(factory, spec.Run, invocation.Json, stdout, stderr);
            var result = invocation.Command switch
            {
                Command.Start start => await factory.StartAsync(start.Item, ct),
                Command.Advance advance => await factory.AdvanceAsync(advance.Run, advance.Wait, ct),
                Command.Status status => await factory.StatusAsync(status.Run, ct),
                _ => throw new System.Diagnostics.UnreachableException($"no handler for {invocation.Command}"),
            };
            await stdout.WriteLineAsync(invocation.Json ? JsonSerializer.Serialize(result, CliJson.Default.AdvanceResult) : result.Message);
            return (int)result.Exit;
        }
        catch (Exception e)
        {
            await stderr.WriteLineAsync($"softwarp: {e.GetType().Name}: {e.Message}");
            return Unexpected;
        }
    }

    static async Task<int> SpecAsync(Factory factory, RunId run, bool json, TextWriter stdout, TextWriter stderr)
    {
        if (factory.Spec(run) is not { } view)
        {
            await stderr.WriteLineAsync($"softwarp: run {run.Value} has no spec package");
            return (int)ExitStatus.InvalidInput;
        }
        await stdout.WriteLineAsync(json ? JsonSerializer.Serialize(view, CliJson.Default.SpecView) : view.Markdown);
        return (int)ExitStatus.Ok;
    }

    /// <summary>`90s`, `5m` or `1h`.</summary>
    static TimeSpan? Duration(string text) =>
        text.Length > 1 && long.TryParse(text[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var count) ? text[^1] switch
        {
            's' => TimeSpan.FromSeconds(count),
            'm' => TimeSpan.FromMinutes(count),
            'h' => TimeSpan.FromHours(count),
            _ => null,
        } : null;
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    WriteIndented = true,
    Converters = [typeof(StringValueJson.Run), typeof(StringValueJson.Hash), typeof(JsonStringEnumConverter<ExitStatus>),
        typeof(JsonStringEnumConverter<Rule>)])]
[JsonSerializable(typeof(AdvanceResult))]
[JsonSerializable(typeof(SpecView))]
sealed partial class CliJson : JsonSerializerContext;
