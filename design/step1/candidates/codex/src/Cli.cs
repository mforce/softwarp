namespace Softwarp;

public abstract record Command
{
    private Command() { }
    public sealed record Run(IntakeRequest Request) : Command;
    public sealed record Advance(RunId Id) : Command;
    public sealed record Status(RunId Id) : Command;
    public sealed record Spec(RunId Id) : Command;
    public sealed record Approval(RunId Id, Uri Receipt) : Command;
}
public sealed record Invocation(Command Command, bool Json);
public static class Cli
{
    /// <summary>Parse explicit verbs and validated identifiers; never load handlers by reflection.</summary>
    public static Invocation Parse(string[] args) => throw new NotImplementedException();
    public static Task<int> ExecuteAsync(Invocation invocation, Factory factory, CancellationToken ct)
        => throw new NotImplementedException();
}
public static class Program
{
    public static Task<int> Main(string[] args) => throw new NotImplementedException();
}
