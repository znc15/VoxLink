using System.ComponentModel;

namespace VoxLink.UI.Controls;

public sealed class HeaderEntry : INotifyPropertyChanged
{
    private string _name;

    public HeaderEntry(string name)
    {
        _name = name;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name
    {
        get => _name;
        set
        {
            if (_name == value)
            {
                return;
            }

            _name = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
        }
    }
}
