using System.Reflection;
using PharmaOS.Platform.Host.ESignature;
using PharmaOS.Platform.Host.Qms;
using PharmaOS.Workflow;

namespace PharmaOS.Platform.Host.Workflow;

/// <summary>
/// The record types whose workflow can be authored. An authored version replaces the transitions of a record type's workflow, but the
/// record type's code still depends on its states (which fields are editable, what counts as closed), so a version must keep the same
/// initial state, use only states the record type knows, permissions the platform defines and signature meanings the record type can sign.
/// </summary>
public static class BuiltInWorkflows
{
    /// <summary>A record type's workflow: its release from code, the entity it runs on and the name it is registered under.</summary>
    public sealed record Target(string Noun, WorkflowDefinition Release, string EntityCode, string Name, IReadOnlyList<string> Meanings);

    private static readonly IReadOnlyDictionary<string, Target> ByCode = new[]
    {
        Of("deviation", DeviationWorkflow.Definition, new DeviationSignable(), "Deviation (standard)"),
        Of("CAPA", CapaWorkflow.Definition, new CapaSignable(), "CAPA (standard)"),
        Of("change control", ChangeWorkflow.Definition, new ChangeSignable(), "Change control (standard)"),
    }.ToDictionary(t => t.Release.Code);

    private static Target Of(string noun, WorkflowDefinition release, WorkflowSignable signable, string name) =>
        new(noun, release, signable.EntityCode, name, signable.Meanings);

    public static Target? Find(string code) => ByCode.GetValueOrDefault(code);

    /// <summary>Every permission the platform defines (the constants on <see cref="Permissions"/>).</summary>
    private static readonly IReadOnlySet<string> KnownPermissions = typeof(Permissions)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f is { IsLiteral: true } && f.FieldType == typeof(string) && f.Name != nameof(Permissions.ClaimType))
        .Select(f => (string)f.GetRawConstantValue()!)
        .ToHashSet();

    public static IReadOnlyList<string> Codes => [.. ByCode.Keys.Order()];

    /// <summary>Problems that stop a draft from running on its record type; empty when it can.</summary>
    public static IReadOnlyList<string> Check(string code, string initialState, IReadOnlyList<TransitionDefinition> transitions)
    {
        if (!ByCode.TryGetValue(code, out var target))
            return [$"No record type uses workflow '{code}'. Authored workflows can replace: {string.Join(", ", Codes)}."];

        var errors = new List<string>();
        if (initialState != target.Release.InitialState)
            errors.Add($"A {target.Noun} starts in '{target.Release.InitialState}', so the initial state must stay '{target.Release.InitialState}'.");

        var known = target.Release.Transitions.SelectMany(t => new[] { t.From, t.To }).ToHashSet();
        foreach (var state in transitions.SelectMany(t => new[] { t.From, t.To }).Distinct().Where(s => !known.Contains(s)).Order())
            errors.Add($"State '{state}' is not a {target.Noun} state. Use: {string.Join(", ", known.Order())}.");

        foreach (var (index, t) in transitions.Index())
        {
            if (!string.IsNullOrWhiteSpace(t.Permission) && !KnownPermissions.Contains(t.Permission))
                errors.Add($"Transition {index + 1} ('{t.Action}') uses permission '{t.Permission}', which the platform does not define.");
            if (t.SignatureMeaning is { } meaning && !target.Meanings.Contains(meaning))
                errors.Add($"Transition {index + 1} ('{t.Action}') asks for signature meaning '{meaning}', which a {target.Noun} cannot be signed with. Use: {string.Join(", ", target.Meanings)}.");
        }
        return errors;
    }
}
