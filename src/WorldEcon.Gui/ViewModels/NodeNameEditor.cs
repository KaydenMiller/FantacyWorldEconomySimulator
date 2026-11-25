using WorldEcon.Application.Globe;
using WorldEcon.Application.Routes;
using WorldEcon.Domain.Geography;

namespace WorldEcon.Gui.ViewModels;

/// <summary>
/// The name box under a selected junction or site's details. Save and Clear go through
/// <see cref="RouteNodeRenameService"/> on a worker thread (which checks the name again: 1–64 characters, no control
/// characters); on success the session takes the new name (<see cref="WorldSession.ApplyRename"/>), raises
/// <see cref="WorldSession.DataChanged"/> once, and <c>onSaved</c> re-shows the node. Errors stay in <see cref="Error"/>.
/// Disabled for a world without a file; Save and Clear are disabled while a simulation run is open
/// (<see cref="WorldSession.ActiveRun"/>: the run holds the file's write lock).
/// </summary>
public sealed class NodeNameEditor : ObservableObject
{
    public const string NoDatabaseText = "No database: renaming needs a world opened from a file.";

    private readonly WorldSession _session;
    private readonly Action<GlobeNode>? _onSaved;
    private GlobeNode _node;
    private string _text;
    private string _error = "";
    private bool _isBusy;

    public NodeNameEditor(WorldSession session, GlobeNode node, Action<GlobeNode>? onSaved = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _node = node ?? throw new ArgumentNullException(nameof(node));
        _onSaved = onSaved;
        _text = node.Name ?? "";
        if (session.Path is null) _error = NoDatabaseText;
    }

    /// <summary>Largest number of characters the name box accepts.</summary>
    public int MaxLength => RouteNode.MaxNameLength;

    /// <summary>False for a world without a file.</summary>
    public bool IsEnabled => _session.Path is not null;

    public string Text
    {
        get => _text;
        set
        {
            if (!Set(ref _text, value ?? "")) return;
            Raise(nameof(CanSave));
        }
    }

    /// <summary>A file-backed world, not busy, no simulation run open, and a trimmed name that is not empty and differs from the current name.</summary>
    public bool CanSave => IsEnabled && !_isBusy && _session.ActiveRun is null && _text.Trim() is { Length: > 0 } t && t != _node.Name;

    /// <summary>A file-backed world, not busy, no simulation run open, and the node has a name.</summary>
    public bool CanClear => IsEnabled && !_isBusy && _session.ActiveRun is null && _node.Name is not null;

    /// <summary>Re-evaluates <see cref="CanSave"/> and <see cref="CanClear"/> after <see cref="WorldSession.ActiveRun"/> changed.</summary>
    internal void RunStateChanged()
    {
        Raise(nameof(CanSave));
        Raise(nameof(CanClear));
    }

    public string Error
    {
        get => _error;
        private set => Set(ref _error, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value)) return;
            Raise(nameof(CanSave));
            Raise(nameof(CanClear));
        }
    }

    public Task SaveAsync() => CanSave ? RenameAsync(_text) : Task.CompletedTask;

    public Task ClearAsync() => CanClear ? RenameAsync(null) : Task.CompletedTask;

    private async Task RenameAsync(string? name)
    {
        if (_session.Path is not { } path) return;
        IsBusy = true;
        try
        {
            var id = _node.Id.ToString();
            var result = await _session.Track(Task.Run(() => RouteNodeRenameService.RenameAsync(path, id, name)));
            if (_session.IsClosed) return;
            if (result.IsError)
            {
                Error = result.FirstError.Description;
                return;
            }

            Error = "";
            _session.ApplyRename(result.Value);
            _node = _session.Node(_node.Id) ?? _node;
            _text = _node.Name ?? "";
            Raise(nameof(Text));
            _session.RaiseDataChanged();
            _onSaved?.Invoke(_node);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
            Raise(nameof(CanSave));
            Raise(nameof(CanClear));
        }
    }
}
