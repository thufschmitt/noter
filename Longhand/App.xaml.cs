using Windows.ApplicationModel.Activation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Longhand;

sealed partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs e)
    {
        if (Window.Current.Content is not Frame frame)
        {
            frame = new Frame();
            Window.Current.Content = frame;
        }
        if (frame.Content is null) frame.Navigate(typeof(MainPage), e.Arguments);
        Window.Current.Activate();
    }
}
