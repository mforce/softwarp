namespace Factory;

/// <summary>Repo commands (DESIGN §11). `{paths}` in TestTargeted is the only template variable.</summary>
public sealed record Commands(string Build, string Test, string TestTargeted, string? Lint, string? Run, string? ServicesUp)
{
    public string Targeted(IEnumerable<string> paths) => throw new NotImplementedException();
}

/// <summary>
/// The repo's WORKFLOW.md, read from trunk at claim time and pinned by commit.
/// Front matter is JSON between `---` fences. JSON is valid YAML 1.2, so a Symphony-format reader still parses the
/// file, and the factory needs no YAML library under Native AOT. The body is markdown guidance pasted into every brief.
/// </summary>
public sealed record Workflow(WorkflowMeta Meta, string Body)
{
    public static Workflow Parse(string text)
    {
        // TODO: text must start with "---\n"; take up to the next "\n---\n"; deserialize via FactoryJson.Default.WorkflowMeta;
        //       rest is Body. Missing fence or missing commands.build/test/test_targeted -> throw with the file path.
        throw new NotImplementedException();
    }
}
