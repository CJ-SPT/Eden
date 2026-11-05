namespace Eden.Protocol.PilotSessions;

// Routine reports stay on the caller's debug sink; important events and explicitly enabled traces have separate sinks.
public sealed record SessionDiagnostics(Action<string> Information, Action<string> Warning, Action<string> Trace);
