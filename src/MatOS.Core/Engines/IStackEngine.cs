using MatOS.Core.Apps;
using MatOS.Core.Entities;

namespace MatOS.Core.Engines;

/// <summary>The things that can be done to a running stack besides deploying, updating and removing it.</summary>
public enum StackAction
{
    Start,
    Stop,
    Restart,
}

/// <summary>
/// Whatever changes a stack of Docker containers: deploys it, updates it, starts and stops it, removes it. Reading how it is doing goes
/// straight to Docker (MatOS has the socket); only changes go through the engine, because the engine is where the stack
/// lives and where the user sees it: MatDock, or MatOS's own compose runner when MatDock cannot be used.
/// </summary>
public interface IStackEngine
{
    /// <summary>Stored with the app: <c>local</c> or <c>matdock</c>.</summary>
    string Id { get; }

    /// <summary>
    /// Saves the stack (compose and env) and starts it. With <paramref name="pull"/> the engine pulls the images first; without, it starts what this machine has
    /// (an image that is missing is still fetched): the install pulled them itself (<see cref="ImagePreflight"/>), and a deploy for new variables (a binding) is no update.
    /// </summary>
    Task DeployAsync(StackSpec spec, bool pull, Action<string> log, CancellationToken ct = default);

    /// <summary>
    /// Pulls the images and re-creates the containers only if an image changed. With a <paramref name="spec"/> the stack is saved first
    /// (a newer catalog entry); without, the stack stays as it is in the engine (the user changed it there).
    /// Returns whether anything changed.
    /// </summary>
    Task<bool> UpdateAsync(InstalledApp app, StackSpec? spec, Action<string> log, CancellationToken ct = default);

    Task ControlAsync(InstalledApp app, StackAction action, Action<string> log, CancellationToken ct = default);

    /// <summary>Removes the containers and the stack; with <paramref name="deleteVolumes"/> the data goes too.</summary>
    Task RemoveAsync(InstalledApp app, bool deleteVolumes, Action<string> log, CancellationToken ct = default);

    /// <summary>The stack as the engine has it now (compose and env), or <c>null</c> if the engine does not know it.</summary>
    Task<StackSpec?> ReadAsync(InstalledApp app, CancellationToken ct = default);
}
