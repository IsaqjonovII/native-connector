namespace AibaShell;

/// <summary>
/// WinUI 3 has no Window.Current, and pages still need to drive shell navigation.
/// One static handle is the cheapest honest answer for a prototype.
/// </summary>
public static class AppShell
{
    public static MainWindow Window { get; set; } = null!;
}
