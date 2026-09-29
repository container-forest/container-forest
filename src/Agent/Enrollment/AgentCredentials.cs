namespace ContainerForest.Agent.Enrollment;

/// <summary>The agent's identity from EnrollResponse.</summary>
public sealed record AgentCredentials(string AgentId, string AgentSecret)
{
    // The generated record ToString would print the secret into any log that formats this object.
    public override string ToString() => $"AgentCredentials {{ AgentId = {AgentId} }}";
}
