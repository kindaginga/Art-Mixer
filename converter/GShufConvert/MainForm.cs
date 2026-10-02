using System.Diagnostics;

namespace GShufConvert;

/// <summary>The converter window: pick where the packs go, drop art mods in, press Convert.</summary>
public sealed class MainForm : Form
{
    private readonly ComboBox _destination = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly TextBox _game = new() { Dock = DockStyle.Fill };
    private readonly TextBox _folder = new() { Dock = DockStyle.Fill };
    private readonly ListView _found = new() { Dock = DockStyle.Fill, View = View.Details, CheckBoxes = true, FullRowSelect = true, ShowItemToolTips = true, HeaderStyle = ColumnHeaderStyle.Nonclickable };
    private readonly Button _find = new() { Text = "Find installed art mods", Width = 140, Height = 48, Margin = new Padding(3, 0, 0, 6) };
    private readonly ListBox _mods = new() { Dock = DockStyle.Fill, AllowDrop = true, SelectionMode = SelectionMode.MultiExtended, IntegralHeight = false };
    private readonly CheckBox _vanilla = new() { Text = "Include the vanilla pictures as their own pack (recommended)", Checked = true, AutoSize = true };
    private readonly CheckBox _split = new() { Text = "Give each alternative picture set its own slider (for mods whose installer lets you pick a set)", AutoSize = true };
    private readonly CheckBox _alr = new() { Text = "Also give the pictures to ALR (Automated Loading Screen Replacer) for its loading screens", AutoSize = true };
    private readonly ToolTip _tips = new() { AutoPopDelay = 30000 };
    private readonly Button _convert = new() { Text = "Convert", Height = 40, Dock = DockStyle.Fill, Font = new Font(FontFamily.GenericSansSerif, 11, FontStyle.Bold) };
    private readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = new Font(FontFamily.GenericMonospace, 9) };
    private readonly Label _status = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
    private readonly Button _browseGame = new() { Text = "Browse", Dock = DockStyle.Fill };
    private readonly Button _browseFolder = new() { Text = "Browse", Dock = DockStyle.Fill };
    private readonly Button _openOutput = new() { Text = "Open output folder", Dock = DockStyle.Fill, Enabled = false };
    private string? _lastOutput;
    private bool _busy;
    private bool _syncing;

    public MainForm()
    {
        Text = "Gallery Shuffle Converter";
        MinimumSize = new Size(820, 680);
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
        grid.Controls.Add(new Label { Text = "Art mods in your mod manager (press Find installed art mods; enabled mods are ticked, untick what you do not want):", AutoSize = true, Margin = new Padding(3, 8, 3, 3) }, 0, row);
        grid.SetColumnSpan(grid.GetControlFromPosition(0, row)!, 3);
        row++;
        _found.Columns.Add("Mod", 280);
        _found.Columns.Add("Where", 90);
        _found.Columns.Add("Status", 70);
        _found.Columns.Add("Contains", 300);
        _found.Resize += (_, _) => _found.Columns[3].Width = Math.Max(200, _found.ClientSize.Width - _found.Columns[0].Width - _found.Columns[1].Width - _found.Columns[2].Width);
        var findPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        findPanel.Controls.Add(_find);
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        grid.Controls.Add(_found, 0, row);
        grid.SetColumnSpan(_found, 2);
        grid.Controls.Add(findPanel, 2, row);
        row++;
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
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 120));
        grid.Controls.Add(_mods, 0, row);
        grid.SetColumnSpan(_mods, 2);
        grid.Controls.Add(sidePanel, 2, row);
        row++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(_vanilla, 0, row); grid.SetColumnSpan(_vanilla, 3); row++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(_split, 0, row); grid.SetColumnSpan(_split, 3); row++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(_alr, 0, row); grid.SetColumnSpan(_alr, 3); row++;
        _tips.SetToolTip(_alr, "Only for players who use ALR. While ALR is active, Gallery Shuffle leaves the loading screens to it.\nTicked: the pack pictures also go into ALR's picture folder, and ALR shows them on its loading screens.\nALR has no sliders, so it shows every picture of every pack. Ticked by itself when ALR is installed where the packs go.");
        _tips.SetToolTip(_split, "Some art mods ask during installation which pictures you want, for example one calendar set or another.\nUnticked: all of a mod's sets go into one pack with one slider, and their pictures are shuffled together.\nTicked: each set becomes its own pack with its own slider. Each category page has room for 10 pack sliders.");
        _tips.SetToolTip(_find, "Looks through the mod folders and downloads of the Mod Organizer 2 instance or Vortex chosen above,\nand lists every mod with textures, materials, models, the main menu video, or music, sounds or interface files that replace the game's own,\nor with a plugin that gives the game's own objects new art. Standalone art mods, which only add objects of their own, are not listed.\nTick a mod to add it to the list of art mods to convert; untick it to take it off again.");
        Disposed += (_, _) => _tips.Dispose();
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        grid.Controls.Add(_convert, 0, row); grid.SetColumnSpan(_convert, 2);
        grid.Controls.Add(_openOutput, 2, row); row++;
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
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
        remove.Click += (_, _) => RemoveMods(_mods.SelectedItems.Cast<string>().ToList());
        _find.Click += async (_, _) => await FindInstalledAsync();
        _found.ItemChecked += (_, e) =>
        {
            if (_syncing || e.Item.Tag is not InstalledMod mod) return;
            if (e.Item.Checked) AddMods([mod.Location]);
            else RemoveMods([mod.Location]);
        };
        DragEnter += OnDragEnter; DragDrop += OnDragDrop;
        _mods.DragEnter += OnDragEnter; _mods.DragDrop += OnDragDrop;
        _convert.Click += async (_, _) => await ConvertAsync();
        _openOutput.Click += (_, _) => { if (_lastOutput != null && Directory.Exists(_lastOutput)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_lastOutput}\"") { UseShellExecute = true }); };
        _status.Text = "Press Find installed art mods or drop art mod archives into the list, check the destination, then press Convert.";
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
        _alr.Checked = Managers.HasAlr(d);
        _found.Items.Clear();
        _find.Enabled = d.Kind != "folder" && !_busy;
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
        SyncFound();
        _status.Text = $"{Report.Count(_mods.Items.Count, "art mod", "art mods")} listed.";
    }

    private void RemoveMods(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            var listed = _mods.Items.Cast<string>().FirstOrDefault(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (listed != null) _mods.Items.Remove(listed);
        }
        SyncFound();
        _status.Text = $"{Report.Count(_mods.Items.Count, "art mod", "art mods")} listed.";
    }

    /// <summary>Ticks exactly the found mods that are in the list of art mods to convert.</summary>
    private void SyncFound()
    {
        _syncing = true;
        try
        {
            foreach (ListViewItem item in _found.Items)
            {
                if (item.Tag is InstalledMod mod) item.Checked = _mods.Items.Cast<string>().Any(p => p.Equals(mod.Location, StringComparison.OrdinalIgnoreCase));
            }
        }
        finally { _syncing = false; }
    }

    /// <summary>Looks through the chosen manager's mod folders and downloads off the window's thread and lists the art mods;
    /// the mods the Mod Organizer 2 profile enables are ticked and added to the list to convert.</summary>
    private async Task FindInstalledAsync()
    {
        if (_busy) return;
        if (_destination.SelectedItem is not Destination d || d.Kind == "folder") return;
        _busy = true;
        _find.Enabled = false;
        _convert.Enabled = false;
        _destination.Enabled = false;
        _found.Items.Clear();
        _status.Text = "Looking for installed art mods.";
        var report = new Report { Sink = line => BeginInvoke(() => { _log.AppendText(line + Environment.NewLine); }) };
        InstalledScan? scan = null;
        string? failure = null;
        await Task.Run(() =>
        {
            try { scan = InstalledMods.Find(d, report, step => BeginInvoke(() => { _status.Text = step; })); }
            catch (Exception e) { failure = e.Message; }
        });
        _busy = false;
        _find.Enabled = true;
        _convert.Enabled = true;
        _destination.Enabled = true;
        if (scan == null)
        {
            _status.Text = "Finding installed art mods failed: " + failure;
            return;
        }
        _syncing = true;
        _found.BeginUpdate();
        try
        {
            foreach (var mod in scan.Mods) _found.Items.Add(new ListViewItem([mod.Name, mod.Where, mod.Status, mod.Summary]) { Tag = mod, ToolTipText = mod.Location });
        }
        finally
        {
            _found.EndUpdate();
            _syncing = false;
        }
        var enabled = scan.Mods.Where(m => m.Enabled == true).Select(m => m.Location).ToList();
        AddMods(enabled);
        var searched = $"{Report.Count(scan.ModFolders, "mod folder", "mod folders")} and {Report.Count(scan.Downloads, "download", "downloads")}";
        var warnings = report.Warnings > 0 ? $" {Report.Count(report.Warnings, "warning", "warnings")} in the log below." : "";
        _status.Text = (scan.Mods.Count == 0 ? $"No art mods found in {searched}." : $"Found {Report.Count(scan.Mods.Count, "art mod", "art mods")} in {searched}. {(enabled.Count > 0 ? $"The {Report.Count(enabled.Count, "enabled one is", "enabled ones are")} ticked; untick what you do not want." : "Tick the ones to convert.")}") + warnings;
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
            AlrImages = _alr.Checked,
            AlrFolder = Managers.AlrImageFolder(d),
            ZipTo = d.Kind == "vortex" ? Path.Combine(d.DownloadsFolder!, Conversion.OutputModName + ".zip") : null,
        };
        _busy = true;
        _convert.Enabled = false;
        _find.Enabled = false;
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
        _find.Enabled = _destination.SelectedItem is Destination { Kind: not "folder" };
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
            "mo2" => $"Done: {Report.Count(packs, "pack", "packs")}. In Mod Organizer 2 press F5, enable \"{Conversion.OutputModName}\" below Gallery Shuffle, then start the game.",
            "vortex" => $"Done: {Report.Count(packs, "pack", "packs")}. In Vortex open Downloads, install \"{Conversion.OutputModName}.zip\", enable it, and let it override Gallery Shuffle.",
            _ => $"Done: {Report.Count(packs, "pack", "packs")} written to {options.OutputFolder}. Install that folder as a mod below Gallery Shuffle.",
        };
        MessageBox.Show(this, _status.Text + (report.Warnings > 0 ? $"\n\n{Report.Count(report.Warnings, "warning", "warnings")}; see GShuf\\convert-report.txt in the output." : ""), Text);
    }
}
