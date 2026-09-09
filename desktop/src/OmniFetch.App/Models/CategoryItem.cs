using CommunityToolkit.Mvvm.ComponentModel;

namespace OmniFetch.App.Models;

public enum CategoryFilterType
{
    All,
    Compressed,
    Documents,
    Music,
    Programs,
    Video,
    Queues,
    Unfinished,
    Finished
}

public partial class CategoryItem : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _iconSource = string.Empty;

    [ObservableProperty]
    private int _count;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private CategoryFilterType _filterType;

    public CategoryItem(string name, string iconSource, CategoryFilterType filterType)
    {
        Name = name;
        IconSource = iconSource;
        FilterType = filterType;
    }
}
