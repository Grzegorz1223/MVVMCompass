using CommunityToolkit.Mvvm.ComponentModel;
using MVVMCompass.Interfaces;

namespace MVVMCompass.Sample;

public sealed class ScenarioLog : ObservableObject
{
    private readonly object gate = new();
    private readonly Queue<string> lines = new();
    private int sequence;
    private string text = "Choose a scenario to see lifecycle events.";
    public string Text { get => text; private set => SetProperty(ref text, value); }
    public string TracePath => Path.Combine(FileSystem.AppDataDirectory, "capability-trace.log");

    public void Write(string message)
    {
        string snapshot;
        lock (gate)
        {
            var line = $"{++sequence:D4} {message}";
            Console.WriteLine($"MVVMCOMPASS {line}");
            File.AppendAllText(TracePath, line + Environment.NewLine);
            lines.Enqueue(line);
            while (lines.Count > 120) lines.Dequeue();
            snapshot = string.Join(Environment.NewLine, lines.Reverse());
        }
        MainThread.BeginInvokeOnMainThread(() => Text = snapshot);
    }
}

// A deterministic stand-in for the consuming application's shared integration and subscriptions.
// IDs, rather than view-model references, keep the trace from retaining dismissed instances.
public sealed class SharedSession
{
    private readonly HashSet<Guid> subscribers = new();
    public int SubscriberCount => subscribers.Count;
    public Guid? ActiveOwner { get; private set; }
    public void Subscribe(Guid id) => subscribers.Add(id);
    public void Unsubscribe(Guid id) => subscribers.Remove(id);
    public void Claim(Guid id)
    {
        if (ActiveOwner is { } owner && owner != id)
            throw new InvalidOperationException("The previous integration owner has not released its session.");
        ActiveOwner = id;
    }
    public void Release(Guid id)
    {
        if (ActiveOwner == id) ActiveOwner = null;
    }
}

public sealed class SampleNotifications(ScenarioLog log) : INotificationService
{
    public void SendNotification(string text, ToastType toastType) => log.Write($"NOTIFICATION {toastType}: {text}");
}
