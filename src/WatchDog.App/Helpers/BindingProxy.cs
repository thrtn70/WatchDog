using System.Windows;

namespace WatchDog.App.Helpers;

/// <summary>
/// Freezable proxy that carries a DataContext binding into WPF elements
/// with separate visual trees (ContextMenu, Popup, ToolTip).
/// Declare as a StaticResource, then bind via {Binding Data.SomeCommand, Source={StaticResource Proxy}}.
/// </summary>
public sealed class BindingProxy : Freezable
{
    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(nameof(Data), typeof(object), typeof(BindingProxy));

    public object Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    protected override Freezable CreateInstanceCore() => new BindingProxy();
}
