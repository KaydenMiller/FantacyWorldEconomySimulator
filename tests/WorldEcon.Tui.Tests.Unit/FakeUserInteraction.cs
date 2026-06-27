using System.Collections.Generic;

namespace WorldEcon.Tui.Tests.Unit;

/// <summary>A scripted <see cref="IUserInteraction"/>: enqueue answers in the order the action asks
/// for them; shown messages are captured for assertions.</summary>
internal sealed class FakeUserInteraction : IUserInteraction
{
    private readonly Queue<string?> _texts = new();
    private readonly Queue<long?> _numbers = new();
    private readonly Queue<int?> _choices = new();
    private readonly Queue<bool> _confirms = new();

    public List<(string Title, IReadOnlyList<string> Lines)> Messages { get; } = [];

    public FakeUserInteraction EnqueueText(string? value) { _texts.Enqueue(value); return this; }
    public FakeUserInteraction EnqueueNumber(long? value) { _numbers.Enqueue(value); return this; }
    public FakeUserInteraction EnqueueChoice(int? value) { _choices.Enqueue(value); return this; }
    public FakeUserInteraction EnqueueConfirm(bool value) { _confirms.Enqueue(value); return this; }

    public Task<string?> AskTextAsync(string title, string prompt, string? initial = null)
        => Task.FromResult(_texts.Count > 0 ? _texts.Dequeue() : initial);

    public Task<long?> AskNumberAsync(string title, string prompt, long? initial = null)
        => Task.FromResult(_numbers.Count > 0 ? _numbers.Dequeue() : initial);

    public Task<int?> AskChoiceAsync(string title, string prompt, IReadOnlyList<string> options)
        => Task.FromResult(_choices.Count > 0 ? _choices.Dequeue() : (int?)null);

    public Task ShowMessageAsync(string title, IReadOnlyList<string> lines)
    {
        Messages.Add((title, lines));
        return Task.CompletedTask;
    }

    public Task<bool> ConfirmAsync(string title, string message)
        => Task.FromResult(_confirms.Count > 0 ? _confirms.Dequeue() : true);
}
