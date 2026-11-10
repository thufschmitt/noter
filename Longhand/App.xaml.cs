using Longhand.Services;
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

    protected override void OnLaunched(LaunchActivatedEventArgs e) => Show(e.Arguments);

    /// <summary>The browser comes back through longhand://, after the user approves a sign-in to a connected app.</summary>
    protected override void OnActivated(IActivatedEventArgs args)
    {
        if (args is ProtocolActivatedEventArgs protocol) OAuthBroker.Deliver(protocol.Uri);
        Show(null);
    }

    static void Show(string? arguments)
    {
        if (Window.Current.Content is not Frame frame)
        {
            frame = new Frame();
            Window.Current.Content = frame;
        }
        if (frame.Content is null) frame.Navigate(typeof(MainPage), arguments);
        Window.Current.Activate();
    }
}
