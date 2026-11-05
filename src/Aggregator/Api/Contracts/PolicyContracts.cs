using ContainerForest.Aggregator.Domain.Policies;

namespace ContainerForest.Aggregator.Api.Contracts;

// The compose policy of an environment (ADR-0012): what the compose files of its stacks may do on its host.
//
//   GET /api/environments/{id}/compose-policy        -> ComposePolicyResponse; whoever sees the environment
//   PUT /api/environments/{id}/compose-policy        -> ComposePolicyResponse; administrators of the system or of the
//                                                       environment; 400 for an invalid path or allowance
//   GET /api/environments/{id}/compose-policy/report -> StackPolicyReportResponse[]; the visible stacks' deployed
//                                                       versions checked against the current policy
//
// Allowances are strings such as "privileged", "capability:SYS_ADMIN", "path-rw:/srv/media" or "port:443"; every
// violation names the one that lifts it. A stack's own allowances are set with PUT /api/stacks/{id}/policy-allowances.

/// <param name="Mode">Off (no checks), Audit (warnings only) or Enforce (violations fail the deployment).</param>
/// <param name="DataRoot">Each stack may mount &lt;DataRoot&gt;/&lt;stack&gt; read-write.</param>
/// <param name="SystemReadOnlyPaths">Host paths every stack may mount read-only.</param>
/// <param name="Allowances">Allowances for every stack of the environment.</param>
/// <param name="ProtectedPaths">Host paths only a stack's allowance with the exact path opens; fixed.</param>
/// <param name="CanChange">Whether the user may change the policy; for hiding buttons only.</param>
public sealed record ComposePolicyResponse(
    ComposePolicyMode Mode,
    string DataRoot,
    IReadOnlyList<string> SystemReadOnlyPaths,
    IReadOnlyList<string> Allowances,
    IReadOnlyList<string> ProtectedPaths,
    bool CanChange);

public sealed record UpdateComposePolicyRequest(
    ComposePolicyMode Mode, string DataRoot, IReadOnlyList<string> SystemReadOnlyPaths, IReadOnlyList<string> Allowances);

/// <param name="Checked">False when no deployment under the policy has succeeded yet, so nothing is known about the stack.</param>
public sealed record StackPolicyReportResponse(
    Guid StackId, string StackName, bool Checked, IReadOnlyList<PolicyViolationResponse> Violations, IReadOnlyList<string> Warnings);

/// <param name="Service">Null for something of the stack as a whole, such as a volume.</param>
/// <param name="Allowance">The allowance that lifts it; null when none does.</param>
public sealed record PolicyViolationResponse(string? Service, string Message, string? Allowance);
