using System.Text.RegularExpressions;
using PharmaOS.Rules;

namespace PharmaOS.Workflow;

/// <summary>
/// Checks a draft workflow definition before it can be submitted for approval. Every problem is reported, not just the first,
/// so an author can fix them in one pass. A definition that passes is safe to construct a <see cref="WorkflowDefinition"/> from.
/// </summary>
public static partial class DefinitionValidator
{
    private static readonly Regex CodePattern = CodeRegex();

    public static IReadOnlyList<string> Validate(string code, int version, string initialState, IReadOnlyList<TransitionDefinition> transitions)
    {
        var errors = new List<string>();

        if (!CodePattern.IsMatch(code ?? ""))
            errors.Add("Code must start with a letter and use only capitals, digits and underscores (for example CAPA_STD).");
        if (version < 1)
            errors.Add("Version must be 1 or higher.");
        if (string.IsNullOrWhiteSpace(initialState))
            errors.Add("An initial state is required.");
        if (transitions.Count == 0)
            errors.Add("A workflow needs at least one transition.");

        foreach (var (index, t) in transitions.Index())
        {
            var where = $"Transition {index + 1} ('{t.Action}')";
            if (string.IsNullOrWhiteSpace(t.Action) || string.IsNullOrWhiteSpace(t.From) || string.IsNullOrWhiteSpace(t.To))
                errors.Add($"Transition {index + 1} needs an action, a from state and a to state.");
            if (string.IsNullOrWhiteSpace(t.Permission))
                errors.Add($"{where} needs a permission, so that someone must be allowed to take it.");
            if (t.Guard is not null)
            {
                try { RuleEvaluator.Validate(t.Guard); }
                catch (RuleException ex)
                {
                    errors.Add($"{where} has an invalid guard: {ex.Message}");
                }
            }
        }

        var duplicates = transitions.GroupBy(t => (t.From, t.Action)).Where(g => g.Count() > 1);
        foreach (var g in duplicates)
            errors.Add($"Action '{g.Key.Action}' is defined more than once from state '{g.Key.From}'.");

        if (!string.IsNullOrWhiteSpace(initialState) && transitions.All(t => t.From != initialState))
            errors.Add($"The initial state '{initialState}' has no outgoing transition.");

        // A state that cannot be reached from the initial state is dead configuration, and it usually means a typo in a state name.
        if (!string.IsNullOrWhiteSpace(initialState) && transitions.Count > 0)
        {
            var reached = new HashSet<string> { initialState };
            var queue = new Queue<string>([initialState]);
            while (queue.Count > 0)
            {
                var state = queue.Dequeue();
                foreach (var next in transitions.Where(t => t.From == state).Select(t => t.To))
                    if (reached.Add(next)) queue.Enqueue(next);
            }
            var states = transitions.SelectMany(t => new[] { t.From, t.To }).Distinct();
            foreach (var unreachable in states.Where(s => !reached.Contains(s)).Order())
                errors.Add($"State '{unreachable}' cannot be reached from the initial state.");
        }

        return errors;
    }

    [GeneratedRegex("^[A-Z][A-Z0-9_]*$")]
    private static partial Regex CodeRegex();
}
