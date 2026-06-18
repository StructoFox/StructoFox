using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using StructoFox.Core;
using StructoFox.Core.Models;

namespace StructoFox.App;

/// <summary>
/// Code exporter: pick a target language and watch the generated source skeleton update live,
/// then copy it or save it to a file. Bodies come from per-method structograms where they exist.
/// The fox's translation desk — one set of structures, ten dialects of code.
/// </summary>
public class ExportWindow : Window
{
    readonly string _projFolder;
    readonly List<CodeEntity> _entities;

    readonly ComboBox _langCombo = new() { MinWidth = 160, MinHeight = 32, CornerRadius = new(4) };
    readonly TextBox  _preview   = new()
    {
        IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
        FontFamily = new("Consolas, Cascadia Mono, monospace"), FontSize = 12,
    };

    // The ten supported languages with friendly labels (Id carries the enum name).
    static readonly (ExportLanguage Lang, string Label)[] Languages =
    {
        (ExportLanguage.CSharp, "C#"), (ExportLanguage.Cpp, "C++"), (ExportLanguage.Java, "Java"),
        (ExportLanguage.TypeScript, "TypeScript"), (ExportLanguage.Python, "Python"), (ExportLanguage.Kotlin, "Kotlin"),
        (ExportLanguage.Swift, "Swift"), (ExportLanguage.Php, "PHP"), (ExportLanguage.Go, "Go"), (ExportLanguage.Rust, "Rust"),
    };

    public ExportWindow(string projFolder, IEnumerable<CodeEntity> entities, string title)
    {
        _projFolder = projFolder;
        _entities   = entities.ToList();

        Title                 = string.Format(Loc.S("Export_Title"), title);
        Width                 = 820;
        Height                = 640;
        MinWidth              = 480;
        MinHeight             = 360;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Ui.ThemeWindow(this);

        Build();
        Regenerate();
    }

    void Build()
    {
        Ui.Theme(_langCombo, TemplatedControl.BackgroundProperty,  "ControlBgBrush");
        Ui.Theme(_langCombo, TemplatedControl.ForegroundProperty,  "SidebarTextBrush");
        Ui.Theme(_langCombo, TemplatedControl.BorderBrushProperty, "ControlBorderBrush");
        foreach (var (_, label) in Languages) _langCombo.Items.Add(label);
        _langCombo.SelectedIndex = 0;
        _langCombo.SelectionChanged += (_, _) => Regenerate();

        Ui.Theme(_preview, TextBox.BackgroundProperty,  "InputBgBrush");
        Ui.Theme(_preview, TextBox.ForegroundProperty,  "SidebarTextBrush");
        Ui.Theme(_preview, TextBox.BorderBrushProperty, "ControlBorderBrush");
        ScrollViewer.SetHorizontalScrollBarVisibility(_preview, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(_preview, ScrollBarVisibility.Auto);

        var top = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = Loc.S("Export_Language"), VerticalAlignment = VerticalAlignment.Center },
                _langCombo,
                new TextBlock { Text = string.Format(Loc.S("Export_Count"), _entities.Count), VerticalAlignment = VerticalAlignment.Center, Opacity = 0.6 },
            },
        };

        var copy  = Ui.Btn(Loc.S("Export_Copy"));
        copy.Click += async (_, _) => { var cb = Clipboard; if (cb is not null) await cb.SetTextAsync(_preview.Text ?? ""); };
        var save  = Ui.Btn(Loc.S("Export_Save"));
        save.Click += async (_, _) => await SaveAsync();
        var close = Ui.Btn(Loc.S("Export_Close")); close.IsCancel = true;
        close.Click += (_, _) => Close();

        var bottom = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8,
            Children = { copy, save, close },
        };

        var grid = new Grid { Margin = new(14), RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        Grid.SetRow(top, 0);     grid.Children.Add(top);
        Grid.SetRow(_preview, 1); _preview.Margin = new(0, 10, 0, 10); grid.Children.Add(_preview);
        Grid.SetRow(bottom, 2);  grid.Children.Add(bottom);
        Content = grid;
    }

    ExportLanguage CurrentLang => Languages[Math.Max(0, _langCombo.SelectedIndex)].Lang;

    // Re-runs the generator for the selected language and shows the result (bodies from structograms).
    void Regenerate()
    {
        try { _preview.Text = CodeExportService.Generate(_entities, CurrentLang, _projFolder); }
        catch (Exception ex) { _preview.Text = "// Export failed: " + ex.Message; }
    }

    // Saves the current preview to a file, defaulting the name + extension to the chosen language.
    async Task SaveAsync()
    {
        var ext  = CodeExportService.FileExtension(CurrentLang);
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title             = Loc.S("Export_Save"),
            SuggestedFileName = "export." + ext,
            DefaultExtension  = ext,
        });
        if (file is null) return;
        try
        {
            await using var stream = await file.OpenWriteAsync();
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(_preview.Text ?? "");
        }
        catch (Exception ex)
        {
            await MessageDialog.Show(this, ex.Message, Loc.S("Export_Save"));
        }
    }
}
