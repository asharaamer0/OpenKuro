using System.Windows;
using System.Windows.Controls;

namespace Kuro.UI;
public sealed class MenuContainerStyleSelector : StyleSelector
{
    public override Style? SelectStyle(object item, DependencyObject container) =>
        Application.Current.TryFindResource(container is Separator ? "ContextSeparator" : "ContextItem") as Style;
}
