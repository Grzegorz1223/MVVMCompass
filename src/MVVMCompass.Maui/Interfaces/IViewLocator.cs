using Microsoft.Maui.Controls;
using System.Reflection;

namespace MVVMCompass.Interfaces
{
    /// <summary>Resolves registered view types and creates their legacy view models through the configured service provider.</summary>
    internal interface IViewLocator
    {
        /// <summary>Copies the supplied view/model registrations for subsequent resolution.</summary>
        void Initialize(Dictionary<Type, Type> registerPairs);
        //void Initialize(Assembly viewModelAssembly, Assembly viewAssembly);
        /// <summary>Creates the registered visual element synchronously through the root service provider; navigation does not own that provider.</summary>
        VisualElement CreateAndBindVEFor<TViewModel>() where TViewModel : ViewModelBase;
        /// <summary>Creates the registered visual element synchronously through the root service provider; navigation does not own that provider.</summary>
        VisualElement CreateAndBindVEFor(Type type);
        /// <summary>Returns the registered visual element type for the supplied model type, or throws when unregistered.</summary>
        Type FindVEForViewModel(Type viewModelType);
        /// <summary>Returns the registered model type for the supplied visual element type, or throws when unregistered.</summary>
        Type FindViewModelForVE(Type page);
    }
}
