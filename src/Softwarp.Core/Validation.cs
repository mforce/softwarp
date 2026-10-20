using System.Collections.Immutable;

namespace Softwarp.Core;

/// <summary>Semantic checks on agent artifacts at import, after the schema parse.</summary>
public static class Validation
{
    /// <summary>Every rule the spec breaks; empty means valid.</summary>
    public static ImmutableArray<string> Validate(SpecArtifact spec)
    {
        var errors = ImmutableArray.CreateBuilder<string>();
        void Require(bool ok, string violation)
        {
            if (!ok) errors.Add(violation);
        }

        Require(!string.IsNullOrWhiteSpace(spec.Markdown), "the spec text is empty");
        Require(!spec.Criteria.IsEmpty, "the spec has no acceptance criterion");
        Require(!spec.TestTargets.IsEmpty, "the spec has no test target");
        foreach (var c in spec.Criteria)
            Require(!string.IsNullOrWhiteSpace(c.Id) && !string.IsNullOrWhiteSpace(c.Text), $"criterion '{c.Id}' has an empty field");
        foreach (var t in spec.Tickets)
            Require(!string.IsNullOrWhiteSpace(t.Id) && !string.IsNullOrWhiteSpace(t.Text), $"ticket '{t.Id}' has an empty field");
        foreach (var h in spec.Holdouts)
            Require(!string.IsNullOrWhiteSpace(h.Scenario) && !string.IsNullOrWhiteSpace(h.Expected), $"a holdout for '{h.CriterionId}' has an empty field");
        foreach (var id in Duplicates(spec.Criteria.Select(c => c.Id)))
            errors.Add($"criterion id '{id}' is not unique");
        foreach (var id in Duplicates(spec.Tickets.Select(t => t.Id)))
            errors.Add($"ticket id '{id}' is not unique");

        var criteria = spec.Criteria.Select(c => c.Id).ToHashSet();
        foreach (var h in spec.Holdouts.Where(h => !criteria.Contains(h.CriterionId)))
            errors.Add($"a holdout refers to unknown criterion '{h.CriterionId}'");

        var tickets = spec.Tickets.Select(t => t.Id).ToHashSet();
        foreach (var t in spec.Tickets)
        foreach (var blocker in t.BlockedBy.Where(b => !tickets.Contains(b)))
            errors.Add($"ticket '{t.Id}' is blocked by unknown ticket '{blocker}'");

        var ordered = new HashSet<string>();
        while (spec.Tickets.FirstOrDefault(t => !ordered.Contains(t.Id)
                   && t.BlockedBy.All(b => ordered.Contains(b) || !tickets.Contains(b))) is { } next)
            ordered.Add(next.Id);
        var cyclic = spec.Tickets.Select(t => t.Id).Where(id => !ordered.Contains(id)).ToList();
        Require(cyclic.Count == 0, $"tickets {string.Join(", ", cyclic)} are in or behind a blocking cycle");

        foreach (var target in spec.TestTargets.Where(t => !IsSafeTestTarget(t)))
            errors.Add($"test target '{target}' is empty, starts with '-' or '/', has a '..' segment, or has a character outside letters, digits and ._/:=~-");
        return errors.ToImmutable();
    }

    /// <summary>Every rule an agent-ready triage breaks; empty means valid.</summary>
    public static ImmutableArray<string> Validate(TriageArtifact triage, RunManifest run)
    {
        if (triage.Decision != TriageDecision.Agent) return [];
        var errors = ImmutableArray.CreateBuilder<string>();
        if (triage.Reproduction.Trunk != run.Trunk) errors.Add("triage did not reproduce on the pinned trunk");
        if (string.IsNullOrWhiteSpace(triage.Reproduction.Command)) errors.Add("the reproduction has no command");
        if (string.IsNullOrWhiteSpace(triage.Reproduction.Evidence)) errors.Add("the reproduction has no evidence");
        if (string.IsNullOrWhiteSpace(triage.PriorRejectionSearch)) errors.Add("triage skipped the prior-rejection search");
        if (string.IsNullOrWhiteSpace(triage.RedundancySearch)) errors.Add("triage skipped the redundancy search");
        return errors.ToImmutable();
    }

    static IEnumerable<string> Duplicates(IEnumerable<string> ids) =>
        ids.CountBy(id => id).Where(group => group.Value > 1).Select(group => group.Key);

    // A target becomes one argv element of a gate command: a leading '-' or '/' would read as an option ('/' is
    // MSBuild's switch prefix), a '..' segment would leave the worktree, and how targets combine is the workflow
    // template's business, so no operators.
    static bool IsSafeTestTarget(string target) =>
        target.Length > 0 && target[0] is not ('-' or '/') && !target.Split('/').Contains("..")
        && target.All(c => char.IsAsciiLetterOrDigit(c) || "._/:=~-".Contains(c));
}
