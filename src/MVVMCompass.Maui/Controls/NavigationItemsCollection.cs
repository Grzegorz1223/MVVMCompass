using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace MVVMCompass;

// Publish one committed membership change. Observers never render an empty or
// partially rebuilt menu while a SetTabs/SetFlyoutItems transaction is committing.
internal sealed class NavigationItemsCollection : ObservableCollection<NavigationItemContext>
{
    internal void ReplaceWith(IEnumerable<NavigationItemContext> source)
    {
        var next = source.ToArray();
        if (this.SequenceEqual(next)) return;
        CheckReentrancy();
        Items.Clear();
        foreach (var item in next) Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
