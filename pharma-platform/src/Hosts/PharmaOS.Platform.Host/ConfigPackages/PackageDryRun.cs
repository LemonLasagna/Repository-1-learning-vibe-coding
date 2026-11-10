using PharmaOS.Gxp;
using PharmaOS.Platform.Host.Workflow;

namespace PharmaOS.Platform.Host.ConfigPackages;

public sealed record DryRunItem(string Code, int Version, string Outcome, IReadOnlyList<string> Problems);

/// <summary>
/// What deploying a package would do here, item by item. Run at import (so a package that cannot work is refused) and again at
/// deploy (because what is released here may have changed since).
/// </summary>
public static class PackageDryRun
{
    public const string Add = "ADD";
    public const string AlreadyReleased = "ALREADY_RELEASED";
    public const string Refused = "REFUSED";

    /// <summary>What deploying each item would do here: add it, find it already released identically, or fail a rule.</summary>
    public static async Task<IReadOnlyList<DryRunItem>> RunAsync(
        WorkflowDraftStore drafts, WorkflowStore wf, PackageDocument document, CancellationToken ct)
    {
        var report = new List<DryRunItem>();
        foreach (var item in document.Items)
        {
            var input = new DraftInput(item.Code, item.Version, item.InitialState, WorkflowStore.TransitionsFromJson(item.Transitions));
            if (BuiltInWorkflows.Find(item.Code) is null)
            {
                report.Add(new(item.Code, item.Version, Refused, [$"This installation has no workflow named {item.Code}."]));
                continue;
            }
            if (await wf.ReleasedVersionAsync(item.Code, item.Version, ct) is { } released)
            {
                var same = CanonicalJson.Sha256Hex(WorkflowStore.TransitionsJson(released.Definition.Transitions)) ==
                           CanonicalJson.Sha256Hex(item.Transitions) && released.Definition.InitialState == item.InitialState;
                report.Add(same
                    ? new(item.Code, item.Version, AlreadyReleased, [])
                    : new(item.Code, item.Version, Refused,
                        [$"Version {item.Version} is already released here with different content. A changed workflow needs a new version number."]));
                continue;
            }
            var (_, problems) = await WorkflowAuthoringEndpoints.CheckAsync(drafts, input, ct);
            report.Add(new(item.Code, item.Version, problems.Count == 0 ? Add : Refused, problems));
        }
        return report;
    }
}
