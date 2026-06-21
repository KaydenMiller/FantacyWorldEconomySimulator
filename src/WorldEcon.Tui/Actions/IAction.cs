namespace WorldEcon.Tui.Actions;

/// <summary>Base for any keyboard-bound action the shell exposes.</summary>
public interface IAction
{
    /// <summary>The single key that triggers this action.</summary>
    char Key { get; }

    /// <summary>Human-readable label shown in the action bar / menu.</summary>
    string Label { get; }
}

/// <summary>An action that is always available (not tied to a selected row).</summary>
public interface IGlobalAction : IAction
{
    Task ExecuteAsync(TuiContext ctx, IUserInteraction ui);
}

/// <summary>An action that operates on the currently-selected row of a specific resource.</summary>
public interface IRowAction : IAction
{
    /// <summary>The canonical resource name this action applies to (e.g. "cities").</summary>
    string ResourceName { get; }

    Task ExecuteAsync(string rowKey, TuiContext ctx, IUserInteraction ui);
}
