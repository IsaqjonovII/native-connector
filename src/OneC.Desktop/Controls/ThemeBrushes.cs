using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace OneC.Desktop.Controls;

/// <summary>
/// Theme-aware brush lookup for controls built in code. <c>Application.Current.Resources[key]</c>
/// answers for the APP theme; a window whose theme is set on its root element (or that follows
/// a different theme) would get the other theme's colours — light zebra rows on a dark table.
/// This resolves against the element's <see cref="FrameworkElement.ActualTheme"/>.
/// </summary>
public static class ThemeBrushes
{
    public static Brush Get(FrameworkElement element, string key)
    {
        string theme = element.ActualTheme == ElementTheme.Dark ? "Dark" : "Light";
        if (Find(Application.Current.Resources, theme, key) is Brush b) return b;
        return (Brush)Application.Current.Resources[key];
    }

    private static object? Find(ResourceDictionary d, string theme, string key)
    {
        if (d.ThemeDictionaries.TryGetValue(theme, out var td) && td is ResourceDictionary rd && rd.TryGetValue(key, out var v))
            return v;
        // XamlControlsResources names its light dictionary "Default".
        if (theme == "Light" && d.ThemeDictionaries.TryGetValue("Default", out var dd) &&
            dd is ResourceDictionary drd && drd.TryGetValue(key, out var dv))
            return dv;
        foreach (var m in d.MergedDictionaries)
            if (Find(m, theme, key) is { } found) return found;
        return null;
    }
}
