using System.Diagnostics;

namespace ArtMixerConvert;

/// <summary>The converter window: pick the mod manager, tick the installed art mods, press Convert.</summary>
public sealed class MainForm : Form
{
    private readonly ComboBox _destination = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly TextBox _game = new() { Dock = DockStyle.Fill };
    private readonly TextBox _folder = new() { Dock = DockStyle.Fill };
    private readonly ListView _found = new() { Dock = DockStyle.Fill, View = View.Details, CheckBoxes = true, FullRowSelect = true, ShowItemToolTips = true, HeaderStyle = ColumnHeaderStyle.Nonclickable };
    private readonly Button _find = new() { Text = "Find installed art mods", Width = 140, Height = 48, Margin = new Padding(3, 0, 0, 6) };
    private readonly ToolTip _tips = new() { AutoPopDelay = 30000 };
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
        SuspendLayout();
        Text = $"{Program.Title} {Program.Build}";
        // Shows the Art Mixer icon in the title bar.
        using (var icon = typeof(MainForm).Assembly.GetManifestResourceStream("ArtMixer.ico")) if (icon != null) Icon = new Icon(icon);
        // Every size below is in pixels at 96 DPI; the window scales them to the display it is on.
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        MinimumSize = new Size(820, 680);
        Size = new Size(960, 760);
        StartPosition = FormStartPosition.CenterScreen;

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

        Add("Mod Manager:", _destination);
        Add("FO4 Folder:", _game, _browseGame);
        Add("Output Folder:", _folder, _browseFolder);
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(new Label { Text = "Art mods installed (anything unticked will not be converted):", AutoSize = true, Margin = new Padding(3, 8, 3, 3) }, 0, row);
        grid.SetColumnSpan(grid.GetControlFromPosition(0, row)!, 3);
        row++;
        _found.Columns.Add("Mod", 320);
        _found.Columns.Add("Status", 80);
        _found.Columns.Add("Contains", 300);
        _found.Resize += (_, _) => FillLastColumn();
        var findPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        findPanel.Controls.Add(_find);
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        grid.Controls.Add(_found, 0, row);
        grid.SetColumnSpan(_found, 2);
        grid.Controls.Add(findPanel, 2, row);
        row++;
        _tips.SetToolTip(_find, "Looks through the mods installed in the Mod Organizer 2 instance or Vortex chosen above\nand lists every mod with art the converter takes. A mod converts as you installed it.");
        Disposed += (_, _) => _tips.Dispose();
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        grid.Controls.Add(_convert, 0, row); grid.SetColumnSpan(_convert, 2);
        grid.Controls.Add(_openOutput, 2, row); row++;
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        grid.Controls.Add(_log, 0, row); grid.SetColumnSpan(_log, 3); row++;
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        grid.Controls.Add(_status, 0, row); grid.SetColumnSpan(_status, 3); row++;
        Controls.Add(grid);
        ResumeLayout(true);

        var detected = Managers.Detect();
        foreach (var d in detected) _destination.Items.Add(d);
        _destination.SelectedIndexChanged += (_, _) => ApplyDestination();

        _browseGame.Click += (_, _) => { using var dlg = new FolderBrowserDialog { Description = "Select the Fallout 4 folder (the one with Fallout4.exe)" }; if (dlg.ShowDialog(this) == DialogResult.OK) _game.Text = dlg.SelectedPath; };
        _browseFolder.Click += (_, _) => { using var dlg = new FolderBrowserDialog { Description = "Select where the Art Mixer Packs folder goes" }; if (dlg.ShowDialog(this) == DialogResult.OK) _folder.Text = Path.Combine(dlg.SelectedPath, Conversion.OutputModName); };
        _find.Click += async (_, _) => await FindInstalledAsync();
        _convert.Click += async (_, _) => await ConvertAsync();
        _openOutput.Click += (_, _) => { if (_lastOutput != null && Directory.Exists(_lastOutput)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_lastOutput}\"") { UseShellExecute = true }); };
        if (detected.Count == 0)
        {
            _find.Enabled = false;
            _convert.Enabled = false;
            _status.Text = "No Mod Organizer 2 instance or Vortex with Fallout 4 was found. Install your art mods with one of them first.";
            return;
        }
        _destination.SelectedItem = Managers.Preferred(detected) ?? _destination.Items[0];
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        SizeColumns();
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        var report = new Report { Sink = line => BeginInvoke(() => { _log.AppendText(line + Environment.NewLine); }) };
        CoreCheckResult? result = null;
        await Task.Run(() =>
        {
            try { result = CoreCheck.Run(report); }
            catch (Exception error) { report.Warn("Art Mixer's own files could not be checked: " + error.Message); }
        });
        if (result?.Message is { } message) MessageBox.Show(this, message, Text);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        BeginInvoke(SizeColumns);
    }

    // Sets the Mod and Status columns to 320 and 80 pixels at 96 DPI, scaled to the window's display.
    private void SizeColumns()
    {
        _found.Columns[0].Width = LogicalToDeviceUnits(320);
        _found.Columns[1].Width = LogicalToDeviceUnits(80);
        FillLastColumn();
    }

    // Gives the Contains column the width the other two leave.
    private void FillLastColumn()
    {
        _found.Columns[2].Width = Math.Max(LogicalToDeviceUnits(200), _found.ClientSize.Width - _found.Columns[0].Width - _found.Columns[1].Width);
    }

    private void ApplyDestination()
    {
        if (_destination.SelectedItem is not Destination d) return;
        _game.Text = d.GameFolder ?? _game.Text;
        _folder.Text = d.Kind == "mo2"
            ? Path.Combine(d.ModsFolder ?? "", Conversion.OutputModName)
            : Path.Combine(Path.GetTempPath(), "ArtMixerConvert-Vortex", Conversion.OutputModName);
        _folder.ReadOnly = d.Kind == "vortex";
        _found.Items.Clear();
        _found.Groups.Clear();
        _find.Enabled = !_busy;
        _status.Text = "Press Find installed art mods, untick what you do not want, then press Convert.";
        ShowConverted();
    }

    // Lists the mods the packs in the output folder were converted from, ticked where the mod's folder is still there, and says so in the status line.
    private void ShowConverted()
    {
        if (Conversion.ReadConverted(_folder.Text) is not { } converted) return;
        _found.BeginUpdate();
        try
        {
            var rows = converted.Mods.Select(mod => new InstalledMod { Name = mod.Name, Location = mod.Folder, Summary = mod.Contains, Group = mod.Group, StatusText = "converted" }).ToList();
            FillGroups(rows, row => Directory.Exists(row.Location));
        }
        finally
        {
            _found.EndUpdate();
        }
        _status.Text = $"{Report.Count(converted.Mods.Count, "mod is", "mods are")} already converted ({converted.Generated}). Press Find installed art mods to pick again, or Convert to rebuild the same packs.";
    }

    /// <summary>Lists the mods under one collapsible group per kind of art, in list order; the props and set dressing and mostly not art groups start collapsed.</summary>
    private void FillGroups(IReadOnlyList<InstalledMod> mods, Func<InstalledMod, bool> ticked)
    {
        _found.Groups.Clear();
        var names = InstalledMods.Groups.Prepend("Converted").Where(name => mods.Any(mod => GroupName(mod) == name));
        foreach (var name in names)
        {
            var count = mods.Count(mod => GroupName(mod) == name);
            _found.Groups.Add(new ListViewGroup(name, $"{name} ({count})")
            {
                CollapsedState = name is InstalledMods.PropsGroup or InstalledMods.NotArtGroup ? ListViewGroupCollapsedState.Collapsed : ListViewGroupCollapsedState.Expanded,
            });
        }
        foreach (var mod in mods)
        {
            _found.Items.Add(new ListViewItem([mod.Name, mod.Status, mod.Summary]) { Tag = mod, ToolTipText = mod.Location, Checked = ticked(mod), Group = _found.Groups[GroupName(mod)] });
        }
    }

    // The group a mod is listed under; "Converted" for a mod the packs recorded without a group.
    private static string GroupName(InstalledMod mod) => mod.Group.Length > 0 ? mod.Group : "Converted";

    /// <summary>Looks through the chosen manager's installed mods off the window's thread and lists the art mods, the ones
    /// the Mod Organizer 2 profile enables and the ones the current packs were converted from ticked, except mods that are mostly not art.</summary>
    private async Task FindInstalledAsync()
    {
        if (_busy) return;
        if (_destination.SelectedItem is not Destination d) return;
        _busy = true;
        _find.Enabled = false;
        _convert.Enabled = false;
        _destination.Enabled = false;
        _found.Items.Clear();
        _found.Groups.Clear();
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
        // Folders of the mods the current packs were converted from.
        var converted = new HashSet<string>(Conversion.ReadConverted(_folder.Text)?.Mods.Where(m => m.Folder.Length > 0).Select(m => Path.GetFullPath(m.Folder)) ?? [], StringComparer.OrdinalIgnoreCase);
        bool Ticked(InstalledMod mod) => mod.Group != InstalledMods.NotArtGroup && (mod.Enabled == true || converted.Contains(Path.GetFullPath(mod.Location)));
        _found.BeginUpdate();
        try
        {
            FillGroups(scan.Mods, Ticked);
        }
        finally
        {
            _found.EndUpdate();
        }
        var ticked = scan.Mods.Count(Ticked);
        var searched = Report.Count(scan.ModFolders, "installed mod", "installed mods");
        var warnings = report.Warnings > 0 ? $" {Report.Count(report.Warnings, "warning", "warnings")} in the log below." : "";
        _status.Text = (scan.Mods.Count == 0
            ? $"No art mods found in {searched}."
            : $"Found {Report.Count(scan.Mods.Count, "art mod", "art mods")} in {searched}; {ticked} ticked. Untick what you do not want, then press Convert.") + warnings;
    }

    private async Task ConvertAsync()
    {
        if (_busy) return;
        if (_destination.SelectedItem is not Destination d) return;
        var inputs = _found.CheckedItems.Cast<ListViewItem>().Select(item => item.Tag).OfType<InstalledMod>().Select(mod => mod.Location).ToList();
        if (inputs.Count == 0) { MessageBox.Show(this, "Press Find installed art mods and tick at least one mod.", Text); return; }
        if (!File.Exists(Path.Combine(_game.Text, "Fallout4.exe"))) { MessageBox.Show(this, "Fallout4.exe was not found in the FO4 Folder.", Text); return; }
        if (_folder.Text.Length == 0) { MessageBox.Show(this, "Choose an output folder.", Text); return; }
        var options = new ConvertOptions
        {
            GameFolder = _game.Text,
            OutputFolder = _folder.Text,
            Inputs = inputs,
            ZipTo = d.Kind == "vortex" ? Path.Combine(d.DownloadsFolder!, Conversion.OutputModName + ".zip") : null,
            Summaries = _found.CheckedItems.Cast<ListViewItem>().Select(item => item.Tag).OfType<InstalledMod>().GroupBy(mod => mod.Location, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.First().Summary, StringComparer.OrdinalIgnoreCase),
            Groups = _found.CheckedItems.Cast<ListViewItem>().Select(item => item.Tag).OfType<InstalledMod>().GroupBy(mod => mod.Location, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.First().Group, StringComparer.OrdinalIgnoreCase),
            EnabledNotConverted = _found.Items.Cast<ListViewItem>().Where(item => !item.Checked).Select(item => item.Tag).OfType<InstalledMod>().Where(mod => mod.Enabled == true).Select(mod => mod.Location).ToList(),
        };
        _busy = true;
        _convert.Enabled = false;
        _find.Enabled = false;
        _log.Clear();
        _status.Text = "Converting; large mods take several minutes.";
        var report = new Report { Sink = line => BeginInvoke(() => { _log.AppendText(line + Environment.NewLine); }) };
        var result = new ConversionResult(0, []);
        string? failure = null;
        await Task.Run(() =>
        {
            try { result = Conversion.Run(options, report); }
            catch (Exception e) { failure = e.Message; }
        });
        _busy = false;
        _convert.Enabled = true;
        _find.Enabled = true;
        _lastOutput = d.Kind == "vortex" ? d.DownloadsFolder : options.OutputFolder;
        _openOutput.Enabled = true;
        if (failure != null)
        {
            _status.Text = "Failed: " + failure;
            MessageBox.Show(this, failure, "Conversion failed");
            return;
        }
        _status.Text = d.Kind == "mo2"
            ? $"Done: {Report.Count(result.Packs, "pack", "packs")}. In Mod Organizer 2 press F5, enable \"{Conversion.OutputModName}\" below Art Mixer, then start the game."
            : $"Done: {Report.Count(result.Packs, "pack", "packs")}. In Vortex open Downloads, install \"{Conversion.OutputModName}.zip\", enable it, and let it override Art Mixer.";
        MessageBox.Show(this, _status.Text + string.Concat(result.SharedWarnings.Select(warning => "\n\n" + warning)) + (result.OwnFilesMessage is { } ownFiles ? "\n\n" + ownFiles : "") + (report.Warnings > 0 ? $"\n\n{Report.Count(report.Warnings, "warning", "warnings")}; see ArtMixer\\convert-report.txt in the output." : ""), Text);
    }
}
