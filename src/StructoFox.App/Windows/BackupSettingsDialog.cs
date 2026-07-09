using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using StructoFox.Core;

namespace StructoFox.App;

/// <summary>Backup preferences (stored globally in <see cref="AppSettings"/>): where project backups go, whether
/// to auto-zip a project on close, and how many zips to keep per project.</summary>
public class BackupSettingsDialog : Window
{
    readonly TextBox  _folder  = new() { MinWidth = 320 };
    readonly CheckBox _onClose = new();
    readonly TextBox  _keep    = new() { MinWidth = 90 };

    public static new Task Show(Window owner) => new BackupSettingsDialog().ShowDialog(owner);

    BackupSettingsDialog()
    {
        Title = Loc.S("Backup_Title");
        Width = 480; SizeToContent = SizeToContent.Height; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Ui.ThemeWindow(this);

        _folder.Text       = AppSettings.BackupFolder;
        _onClose.IsChecked = AppSettings.BackupOnClose;
        _onClose.Content   = Loc.S("Backup_OnClose");
        _keep.Text         = AppSettings.BackupKeep.ToString();
        foreach (var b in new[] { _folder, _keep })
        {
            Ui.Theme(b, TextBox.BackgroundProperty,  "InputBgBrush");
            Ui.Theme(b, TextBox.ForegroundProperty,  "SidebarTextBrush");
            Ui.Theme(b, TextBox.BorderBrushProperty, "ControlBorderBrush");
        }

        var browse = Ui.Btn(Loc.S("Backup_Browse")); browse.Margin = new(8, 0, 0, 0);
        browse.Click += async (_, _) =>
        {
            var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            { AllowMultiple = false, Title = Loc.S("Backup_PickFolder") });
            if (picked.FirstOrDefault()?.TryGetLocalPath() is { Length: > 0 } p) _folder.Text = p;
        };
        var folderRow = new Grid { ColumnDefinitions = new("*,Auto") };
        Grid.SetColumn(_folder, 0); folderRow.Children.Add(_folder);
        Grid.SetColumn(browse, 1);  folderRow.Children.Add(browse);

        var ok = Ui.Btn(Loc.S("Common_OK")); ok.IsDefault = true;
        ok.Click += async (_, _) =>
        {
            var folder = (_folder.Text ?? "").Trim();
            // A StructoFox project folder is a bad backup root (backups would nest inside a project, and
            // zipping it could recurse). Reject it right away instead of silently skipping backups later.
            if (folder.Length > 0 && ProjectService.IsProject(folder))
            {
                await MessageDialog.Show(this, Loc.S("Backup_ErrIsProject"), Loc.S("Backup_ErrTitle"));
                return;   // keep the dialog open so the user can pick another folder
            }
            AppSettings.BackupFolder  = folder;
            AppSettings.BackupOnClose = _onClose.IsChecked == true;
            if (int.TryParse((_keep.Text ?? "").Trim(), out var k) && k > 0) AppSettings.BackupKeep = k;
            Close();
        };
        var cancel = Ui.Btn(Loc.S("Common_Cancel")); cancel.IsCancel = true; cancel.Click += (_, _) => Close();

        TextBlock Lbl(string t) { var b = new TextBlock { Text = t, TextWrapping = Avalonia.Media.TextWrapping.Wrap }; Ui.Theme(b, TextBlock.ForegroundProperty, "ContentTextBrush"); return b; }

        Content = new StackPanel
        {
            Margin = new(18), Spacing = 10,
            Children =
            {
                Lbl(Loc.S("Backup_Folder")), folderRow,
                _onClose,
                Lbl(Loc.S("Backup_Keep")), _keep,
                Lbl(Loc.S("Backup_Note")),
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8, Margin = new(0, 6, 0, 0), Children = { cancel, ok } },
            },
        };
    }
}
