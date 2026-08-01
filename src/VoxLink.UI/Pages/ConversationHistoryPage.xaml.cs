using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VoxLink.UI.Core.Models;
using VoxLink.UI.Core.ViewModels;

namespace VoxLink.UI.Pages;

public sealed partial class ConversationHistoryPage : Page
{
    public ConversationHistoryPage()
    {
        InitializeComponent();
        Loaded += ConversationHistoryPage_Loaded;
        Unloaded += ConversationHistoryPage_Unloaded;
    }

    public AppController Controller => App.Controller;

    private void ConversationHistoryPage_Loaded(object sender, RoutedEventArgs args)
    {
        Controller.Messages.CollectionChanged += Messages_CollectionChanged;
        Refresh();
    }

    private void ConversationHistoryPage_Unloaded(object sender, RoutedEventArgs args) =>
        Controller.Messages.CollectionChanged -= Messages_CollectionChanged;

    private void Messages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs args) =>
        Refresh();

    private void Refresh()
    {
        var hasMessages = Controller.Messages.Count > 0;
        EmptyState.Visibility = hasMessages ? Visibility.Collapsed : Visibility.Visible;
        ConversationList.Visibility = hasMessages ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ClearMessages_Click(object sender, RoutedEventArgs args) =>
        Controller.ClearMessages();

    private async void SpeakMessage_Click(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: ConversationMessage message })
        {
            await Controller.SpeakAsync(message);
        }
    }
}
