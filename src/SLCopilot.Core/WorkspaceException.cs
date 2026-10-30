namespace SLCopilot.Core;

/// <summary>A deliberately sanitized, actionable error that may be returned to the model.</summary>
public sealed class WorkspaceException(string message) : IOException(message);
