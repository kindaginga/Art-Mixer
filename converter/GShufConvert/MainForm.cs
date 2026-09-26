using System.Diagnostics;

namespace GShufConvert;

/// <summary>The converter window: pick where the packs go, drop art mods in, press Convert.</summary>
public sealed class MainForm : Form
{
    private readonly ComboBox _destination = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly TextBox _game = new() { Dock = DockStyle.Fill };
    private readonly TextBox _folder = new() { Dock = DockStyle.Fill };
    private readonly ListBox _mods = new() { Dock = DockStyle.Fill, AllowDrop = true, SelectionMode = SelectionMode.MultiExtended, IntegralHeight = false };
    private readonly CheckBox _vanilla = new() { Text = "Include the vanilla pictures as their own pack (recommended)", Checked = true, AutoSize = true };
    private readonly CheckBox _split = new() { Text = "One pack per FOMOD option instead of one per mod", AutoSize = true };
    private readonly Button _convert = new() { Text = "Convert", Height = 40, Dock = DockStyle.Fill, Font = new Font(FontFamily.GenericSansSerif, 11, FontStyle.Bold) };
    private readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = new Font(FontFamily.GenericMonospace, 9) };
    private readonly Label _status = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
    private readonly Button _browseGame = new() { Text = "Browse", Dock = DockStyle.Fill };
    private readonly Button _browseFolder = new() { Text = "Browse", Dock = DockStyle.Fill };
    private readonly Button _openOutput = new() { Text = "Open output folder", Dock = DockStyle.Fill, Enabled = false };
    private string? _lastOutput;
    private bool _busy;

    public MainForm()
    {
        Text = "Gallery Shuffle Converter";
        MinimumSize = new Size(820, 640);
        Size = new Size(960, 760);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        AllowDrop = true;

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Padding = new Padding(12) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));

        int row = 0;
        void Add(string label, Control control, Control? side = null, int height = 34)
        {
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            grid.Controls.Add(new Label { Text = label, TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill }, 0, row);
            grid.Controls.Add(control, 1, row);
            if (side != null) grid.Controls.Add(side, 2, row);
            row++;
        }

        Add("Deliver packs to", _destination);
        Add("Fallout 4 folder", _game, _browseGame);
        Add("Output folder", _folder, _browseFolder);
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(new Label { Text = "Art mods to convert (drop archives or folders here; every run rebuilds the packs, so list all of them each time):", AutoSize = true, Margin = new Padding(3, 8, 3, 3) }, 0, row);
        grid.SetColumnSpan(grid.GetControlFromPosition(0, row)!, 3);
        row++;
        var addFiles = new Button { Text = "Add archives", Width = 140, Height = 32, Margin = new Padding(3, 0, 0, 6) };
        var addFolder = new Button { Text = "Add folder", Width = 140, Height = 32, Margin = new Padding(3, 0, 0, 6) };
        var remove = new Button { Text = "Remove selected", Width = 140, Height = 32, Margin = new Padding(3, 0, 0, 6) };
        var sidePanel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        sidePanel.Controls.Add(addFiles);
        sidePanel.Controls.Add(addFolder);
        sidePanel.Controls.Add(remove);
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
        grid.Controls.Add(_mods, 0, row);
        grid.SetColumnSpan(_mods, 2);
        grid.Controls.Add(sidePanel, 2, row);
        row++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(_vanilla, 0, row); grid.SetColumnSpan(_vanilla, 3); row++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(_split, 0, row); grid.SetColumnSpan(_split, 3); row++;
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        grid.Controls.Add(_convert, 0, row); grid.SetColumnSpan(_convert, 2);
        grid.Controls.Add(_openOutput, 2, row); row++;
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
        grid.Controls.Add(_log, 0, row); grid.SetColumnSpan(_log, 3); row++;
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        grid.Controls.Add(_status, 0, row); grid.SetColumnSpan(_status, 3); row++;
        Controls.Add(grid);

        var detected = Managers.Detect();
        foreach (var d in detected) _destination.Items.Add(d);
        _destination.Items.Add(new Destination { Label = "A folder I choose (manual install)", Kind = "folder", GameFolder = Managers.RegistryGameFolder() });
        _destination.SelectedIndexChanged += (_, _) => ApplyDestination();
        var preferred = Managers.Preferred(detected);
        _destination.SelectedItem = preferred ?? _destination.Items[0];

        _browseGame.Click += (_, _) => { using var dlg = new FolderBrowserDialog { Description = "Select the Fallout 4 folder (the one with Fallout4.exe)" }; if (dlg.ShowDialog(this) == DialogResult.OK) _game.Text = dlg.SelectedPath; };
        _browseFolder.Click += (_, _) => { using var dlg = new FolderBrowserDialog { Description = "Select where the Gallery Shuffle Packs folder goes" }; if (dlg.ShowDialog(this) == DialogResult.OK) _folder.Text = Path.Combine(dlg.SelectedPath, Conversion.OutputModName); };
        addFiles.Click += (_, _) =>
        {
            using var dlg = new OpenFileDialog { Multiselect = true, Filter = "Mod archives (*.zip;*.7z;*.rar)|*.zip;*.7z;*.rar|All files|*.*", Title = "Add art mod archives" };
            if (dlg.ShowDialog(this) == DialogResult.OK) AddMods(dlg.FileNames);
        };
        addFolder.Click += (_, _) => { using var dlg = new FolderBrowserDialog { Description = "Select an unpacked art mod folder" }; if (dlg.ShowDialog(this) == DialogResult.OK) AddMods([dlg.SelectedPath]); };
        remove.Click += (_, _) => { foreach (var item in _mods.SelectedItems.Cast<object>().ToList()) _mods.Items.Remove(item); };
        DragEnter += OnDragEnter; DragDrop += OnDragDrop;
        _mods.DragEnter += OnDragEnter; _mods.DragDrop += OnDragDrop;
        _convert.Click += async (_, _) => await ConvertAsync();
        _openOutput.Click += (_, _) => { if (_lastOutput != null && Directory.Exists(_lastOutput)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_lastOutput}\"") { UseShellExecute = true }); };
        _status.Text = "Drop art mod archives into the list, check the destination, then press Convert.";
    }

    private void ApplyDestination()
    {
        if (_destination.SelectedItem is not Destination d) return;
        _game.Text = d.GameFolder ?? _game.Text;
        _folder.Text = d.Kind switch
        {
            "mo2" => Path.Combine(d.ModsFolder ?? "", Conversion.OutputModName),
            "vortex" => Path.Combine(Path.GetTempPath(), "GShufConvert-Vortex", Conversion.OutputModName),
            _ => _folder.Text,
        };
        _folder.ReadOnly = d.Kind == "vortex";
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths) AddMods(paths);
    }

    private void AddMods(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (_mods.Items.Cast<string>().Any(p => p.Equals(path, StringComparison.OrdinalIgnoreCase))) continue;
            _mods.Items.Add(path);
        }
        _status.Text = $"{_mods.Items.Count} art mods listed.";
    }

    private async Task ConvertAsync()
    {
        if (_busy) return;
        if (_destination.SelectedItem is not Destination d) return;
        if (_mods.Items.Count == 0) { MessageBox.Show(this, "Add at least one art mod archive or folder.", Text); return; }
        if (!File.Exists(Path.Combine(_game.Text, "Fallout4.exe"))) { MessageBox.Show(this, "Fallout4.exe was not found in the Fallout 4 folder.", Text); return; }
        if (_folder.Text.Length == 0) { MessageBox.Show(this, "Choose an output folder.", Text); return; }
        var options = new ConvertOptions
        {
            GameFolder = _game.Text,
            OutputFolder = _folder.Text,
            Inputs = _mods.Items.Cast<string>().ToList(),
            IncludeVanilla = _vanilla.Checked,
            SplitOptions = _split.Checked,
            ZipTo = d.Kind == "vortex" ? Path.Combine(d.DownloadsFolder!, Conversion.OutputModName + ".zip") : null,
        };
        _busy = true;
        _convert.Enabled = false;
        _log.Clear();
        _status.Text = "Converting; large archives take several minutes.";
        var report = new Report { Sink = line => BeginInvoke(() => { _log.AppendText(line + Environment.NewLine); }) };
        int packs = 0;
        string? failure = null;
        await Task.Run(() =>
        {
            try { packs = Conversion.Run(options, report); }
            catch (Exception e) { failure = e.Message; }
        });
        _busy = false;
        _convert.Enabled = true;
        _lastOutput = d.Kind == "vortex" ? d.DownloadsFolder : options.OutputFolder;
        _openOutput.Enabled = true;
        if (failure != null)
        {
            _status.Text = "Failed: " + failure;
            MessageBox.Show(this, failure, "Conversion failed");
            return;
        }
        _status.Text = d.Kind switch
        {
            "mo2" => $"Done: {packs} packs. In Mod Organizer 2 press F5, enable \"{Conversion.OutputModName}\" below Gallery Shuffle, then start the game.",
            "vortex" => $"Done: {packs} packs. In Vortex open Downloads, install \"{Conversion.OutputModName}.zip\", enable it, and let it override Gallery Shuffle.",
            _ => $"Done: {packs} packs written to {options.OutputFolder}. Install that folder as a mod below Gallery Shuffle.",
        };
        MessageBox.Show(this, _status.Text + (report.Warnings > 0 ? $"\n\n{report.Warnings} warnings; see GShuf\\convert-report.txt in the output." : ""), Text);
    }
}
