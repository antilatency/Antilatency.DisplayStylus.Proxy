namespace Antilatency.DisplayStylus.Proxy.Adn;

internal sealed class DriverCommand {
    public DriverCommand(Action action, CancellationToken cancellationToken) {
        Action = action;
        CancellationToken = cancellationToken;
    }

    public Action Action { get; }
    public CancellationToken CancellationToken { get; }
    public TaskCompletionSource Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
