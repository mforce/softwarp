using System.Collections.Immutable;

namespace Softwarp.Core;

/// <summary>WORKFLOW.md as read at the pinned trunk commit: JSON front matter between `---` lines, then the prompt
/// body.</summary>
public static class WorkflowFile
{
    const string Targets = "{targets}";
    const int MaxSlug = 40;

    /// <summary>Fails closed: any problem throws, naming every rule the file breaks.</summary>
    /// <exception cref="FormatException">The file is not a valid WORKFLOW.md.</exception>
    public static ParsedWorkflow Parse(string text)
    {
        var lines = text.TrimStart('\uFEFF').ReplaceLineEndings("\n").Split('\n');
        var end = Array.IndexOf(lines, "---", 1);
        if (lines[0] != "---" || end < 0)
            throw new FormatException("WORKFLOW.md must start with JSON front matter between two '---' lines");

        WorkflowSettings settings;
        try
        {
            settings = Contracts.Read(System.Text.Encoding.UTF8.GetBytes(string.Join('\n', lines[1..end])),
                Contracts.Default.WorkflowSettings);
        }
        catch (System.Text.Json.JsonException e)
        {
            throw new FormatException($"WORKFLOW.md front matter: {e.Message}", e);
        }

        var errors = new List<string>();
        var warnings = ImmutableArray.CreateBuilder<string>();
        if (settings.Trust is < 0 or > 2) errors.Add($"trust {settings.Trust} is outside the rungs step 1 allows (0 to 2)");
        if (BranchProblem(settings.Branch) is { } branch) errors.Add(branch);
        var commands = settings.Commands;
        foreach (var (name, argv) in new[]
                 {
                     ("build", (ImmutableArray<string>?)commands.Build), ("test_targeted", commands.TestTargeted),
                     ("test", commands.Test), ("lint", commands.Lint), ("run", commands.Run), ("services_up", commands.ServicesUp),
                 })
        {
            if (argv is not { } command) continue;
            errors.AddRange(CommandProblems(name, command));
            if (command.Contains(Targets) && Path.GetFileName(command[0]) is "make" or "gmake" or "just")
                warnings.Add($"{name} passes {Targets} to {Path.GetFileName(command[0])}: a test target may hold '=', so a target " +
                    "such as SHELL=/bin/true sets a variable instead of naming a test");
        }
        if (!commands.TestTargeted.Contains(Targets))
            errors.Add($"test_targeted must pass the approved test targets as a {Targets} argv element");
        if (errors.Count > 0) throw new FormatException($"WORKFLOW.md: {string.Join("; ", errors)}");

        return new ParsedWorkflow(new Workflow(Spec(commands.Build), Spec(commands.TestTargeted)), settings.Branch,
            warnings.ToImmutable());
    }

    /// <summary>The run branch: `{issue}` becomes the issue number and `{slug}` a lowercase ASCII slug of the title.</summary>
    public static string Branch(string template, WorkItem item)
    {
        var slug = new string(item.Title.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray());
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        slug = slug.Trim('-');
        slug = slug[..Math.Min(slug.Length, MaxSlug)].TrimEnd('-');
        return template.Replace("{issue}", item.Id.Number.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Replace("{slug}", slug.Length > 0 ? slug : "item");
    }

    static CommandSpec Spec(ImmutableArray<string> argv) => new(argv[0], argv[1..]);

    static IEnumerable<string> CommandProblems(string name, ImmutableArray<string> argv)
    {
        if (argv.IsEmpty || argv[0].Length == 0)
        {
            yield return $"{name} has no executable";
            yield break;
        }
        if (argv.Any(arg => arg.Contains('\0'))) yield return $"{name} has a NUL in an argv element";
        if (argv.Any(arg => arg.Contains(Targets) && arg != Targets))
            yield return $"{name} must use {Targets} as a whole argv element";
        if (argv[0] == Targets) yield return $"{name} may not use {Targets} as the executable";
        // ponytail: catches a shell run directly; `env sh -c`, `python -c` and similar wrappers are not detected.
        else if (argv.Contains(Targets) && IsShell(argv[0]) && argv.Skip(1).Any(IsDashC))
            yield return $"{name} may not pass {Targets} to a shell running a -c script";
    }

    static bool IsShell(string executable) => Path.GetFileName(executable) is "sh" or "bash" or "zsh" or "dash";

    /// <summary>`-c` alone or inside a cluster of short options such as `-ec`.</summary>
    static bool IsDashC(string arg) => arg.Length > 1 && arg[0] == '-' && arg.Contains('c') && arg.Skip(1).All(char.IsAsciiLetter);

    static string? BranchProblem(string template)
    {
        if (!template.Contains("{issue}")) return "branch must contain {issue}, so a run finds its branch and PR again";
        var plain = template.Replace("{issue}", "").Replace("{slug}", "");
        return plain.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '/' or '-')
               && template[0] is not ('-' or '/') && !template.Contains("..")
            ? null
            : "branch may hold only letters, digits, '._/-', {issue} and {slug}, may not start with '-' or '/', and may not contain '..'";
    }
}

/// <summary>Warnings name risks the file may take on purpose; the run still starts.</summary>
public sealed record ParsedWorkflow(Workflow Workflow, string BranchTemplate, ImmutableArray<string> Warnings);

/// <summary>The front matter keys of DESIGN.md §11. Every key is listed so an unknown one is rejected.</summary>
public sealed record WorkflowSettings(WorkflowCommands Commands, string Branch, int Trust = 1, string? Tracker = null,
    string? VerifySkill = null, string? ReviewBot = null, int? WipLimit = null,
    ImmutableArray<WorkflowRoutine>? Routines = null);
public sealed record WorkflowCommands(ImmutableArray<string> Build, ImmutableArray<string> TestTargeted,
    ImmutableArray<string>? Test = null, ImmutableArray<string>? Lint = null, ImmutableArray<string>? Run = null,
    ImmutableArray<string>? ServicesUp = null);
public sealed record WorkflowRoutine(string Name, string Every, string Skill);
