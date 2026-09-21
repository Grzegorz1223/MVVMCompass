using System.Collections.ObjectModel;

namespace MVVMCompass;

/// <summary>A registered initial destination selected without constructing its view or model.</summary>
public sealed class InitialRoot
{
    /// <summary>Creates a root selection and snapshots its parameters. Register the concrete model type with UseMVVMCompass.</summary>
    public InitialRoot(Type viewModelType, IReadOnlyDictionary<string, object>? parameters = null)
    {
        ArgumentNullException.ThrowIfNull(viewModelType);
        if (!typeof(ViewModelBase).IsAssignableFrom(viewModelType) || viewModelType.IsAbstract || viewModelType.ContainsGenericParameters)
            throw new ArgumentException("An initial root must be a concrete ViewModelBase type.", nameof(viewModelType));
        ViewModelType = viewModelType;
        Parameters = parameters == null ? null : new ReadOnlyDictionary<string, object>(
            new Dictionary<string, object>(parameters, parameters is Dictionary<string, object> dictionary ? dictionary.Comparer : StringComparer.Ordinal));
    }

    /// <summary>Gets the model type to resolve through the existing AOT-safe destination registration.</summary>
    public Type ViewModelType { get; }
    /// <summary>Gets a read-only snapshot of the initial parameters. Referenced parameter values are not deep-cloned.</summary>
    public IReadOnlyDictionary<string, object>? Parameters { get; }
    /// <summary>Selects a registered model without constructing it.</summary>
    public static InitialRoot For<TViewModel>(IReadOnlyDictionary<string, object>? parameters = null) where TViewModel : ViewModelBase =>
        new(typeof(TViewModel), parameters);
}
