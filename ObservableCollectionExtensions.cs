using System.Collections.ObjectModel;

namespace Sharm;

internal static class ObservableCollectionExtensions
{
    internal static void AddRange<T>(this ObservableCollection<T> collection, IEnumerable<T> values)
    { foreach (var value in values) collection.Add(value); }

    internal static void InsertRange<T>(this ObservableCollection<T> collection, int index, IEnumerable<T> values)
    { foreach (var value in values) collection.Insert(index++, value); }

    internal static void RemoveRange<T>(this ObservableCollection<T> collection, int index, int count)
    { for (var removed = 0; removed < count; removed++) collection.RemoveAt(index); }
}
