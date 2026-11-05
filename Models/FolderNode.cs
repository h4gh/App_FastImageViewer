using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using FastImageViewer.Services;

namespace FastImageViewer.Models;

public sealed class FolderNode : INotifyPropertyChanged
{
    private bool _isExpanded;
    private bool _isSelected;
    private bool _childrenLoaded;

    public FolderNode(string fullPath, string displayName, bool isDrive = false, string? customIcon = null)
    {
        FullPath = fullPath;
        DisplayName = displayName;
        IsDrive = isDrive;
        CustomIcon = customIcon;

        if (!string.IsNullOrEmpty(fullPath))
        {
            // Unique placeholder instance per node so WPF TreeView ItemContainerGenerator never collides
            Children.Add(new FolderNode(string.Empty, "Načítání..."));
        }
    }

    public string FullPath { get; }
    public string DisplayName { get; }
    public bool IsDrive { get; }
    public string? CustomIcon { get; }

    public string Icon => !string.IsNullOrEmpty(CustomIcon)
        ? CustomIcon
        : (IsDrive ? "💽" : (_isExpanded ? "📂" : "📁"));

    public ObservableCollection<FolderNode> Children { get; } = [];

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded != value)
            {
                _isExpanded = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Icon));

                if (_isExpanded)
                {
                    EnsureChildrenLoaded();
                }
            }
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged();
            }
        }
    }

    public void EnsureChildrenLoaded(bool forceRefresh = false)
    {
        if (_childrenLoaded && !forceRefresh)
            return;

        _childrenLoaded = true;
        Children.Clear();

        if (string.IsNullOrEmpty(FullPath) || !Directory.Exists(FullPath))
            return;

        try
        {
            var dirInfo = new DirectoryInfo(FullPath);
            var validSubDirs = new List<DirectoryInfo>();

            foreach (var sub in dirInfo.EnumerateDirectories())
            {
                try
                {
                    var attrs = sub.Attributes;
                    if ((attrs & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                    {
                        validSubDirs.Add(sub);
                    }
                }
                catch
                {
                    // Skip individual inaccessible system/junction directory
                }
            }

            validSubDirs.Sort((a, b) => NaturalStringComparer.Instance.Compare(a.Name, b.Name));

            foreach (var sub in validSubDirs)
            {
                Children.Add(new FolderNode(sub.FullName, sub.Name));
            }
        }
        catch
        {
            // Ignore unauthorized / inaccessible folders
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
