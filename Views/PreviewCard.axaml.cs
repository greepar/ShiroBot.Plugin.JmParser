using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace ShiroBot.JmParser.Views;

public partial class PreviewCard : UserControl
{
    public PreviewCard()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
