using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Shell;

namespace LocalAccountManager;

public static class ThemeChrome
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(ThemeChrome), new PropertyMetadata(false, Changed));
    public static void SetEnabled(DependencyObject target, bool value) => target.SetValue(EnabledProperty, value);
    public static bool GetEnabled(DependencyObject target) => (bool)target.GetValue(EnabledProperty);
    private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not Window window || args.NewValue is not true) return;
        var chrome = new WindowChrome { CaptionHeight = 38, GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(8), UseAeroCaptionButtons = false };
        void SizeChanged(object? sender, EventArgs e) => chrome.ResizeBorderThickness =
            window.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip ? new Thickness(6) : new Thickness(0);
        SizeChanged(null, EventArgs.Empty);
        WindowChrome.SetWindowChrome(window, chrome);
        var descriptor = DependencyPropertyDescriptor.FromProperty(Window.ResizeModeProperty, typeof(Window));
        descriptor.AddValueChanged(window, SizeChanged);
        window.Closed += (_, _) => descriptor.RemoveValueChanged(window, SizeChanged);
        window.CommandBindings.Add(new CommandBinding(SystemCommands.CloseWindowCommand, (_, _) => SystemCommands.CloseWindow(window)));
        window.CommandBindings.Add(new CommandBinding(SystemCommands.MinimizeWindowCommand, (_, _) => SystemCommands.MinimizeWindow(window),
            (_, e) => e.CanExecute = window.ResizeMode != ResizeMode.NoResize));
        window.CommandBindings.Add(new CommandBinding(SystemCommands.MaximizeWindowCommand, (_, _) => SystemCommands.MaximizeWindow(window),
            (_, e) => e.CanExecute = window.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip));
        window.CommandBindings.Add(new CommandBinding(SystemCommands.RestoreWindowCommand, (_, _) => SystemCommands.RestoreWindow(window)));
        window.CommandBindings.Add(new CommandBinding(SystemCommands.ShowSystemMenuCommand,
            (_, _) => SystemCommands.ShowSystemMenu(window, window.PointToScreen(new Point(8, 38)))));
        window.SourceInitialized += (_, _) =>
        {
            try
            {
                nint handle = new WindowInteropHelper(window).Handle;
                int dark = 1, border = 0x3E312A;
                DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
                DwmSetWindowAttribute(handle, 34, ref border, sizeof(int));
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        };
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
}
