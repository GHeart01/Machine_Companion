using MakinoMCP.Core.Cell;
using MakinoMCP.Core.Settings;

namespace MakinoMCP.Setup;

/// <summary>Where a company enters its cell database login and the folders Claude may read, then gets the two
/// snippets that connect Claude. Plain WinForms in the light theme, in system colours and the system font, so high
/// contrast and display scaling apply (it never follows Windows dark mode). The rules live in <see cref="SetupChecks"/>.</summary>
internal sealed class SetupForm : Form
{
    private const string Title = "MakinoMCP setup";
    private static readonly TimeSpan TestCap = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FolderProbeCap = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CopiedFor = TimeSpan.FromSeconds(2);
    private const string NextStart = "Claude uses these settings the next time it starts MakinoMCP.";

    private readonly string _settingsPath;
    private readonly CancellationTokenSource _closing = new();
    private Limits _limits = new();
    private int _connectTimeoutSeconds = new MakinoLogin("", "", false, "", "").ConnectTimeoutSeconds;
    private bool _dirty;
    private int _tabIndex;

    // Cell database
    private readonly TextBox _server = new() { MinimumSize = new Size(280, 0) };
    private readonly TextBox _database = new() { MinimumSize = new Size(280, 0) };
    private readonly CheckBox _windowsLogin = new() { Text = "Use &Windows login (the account running Claude)", AutoSize = true };
    private readonly Label _userLabel = FieldLabel("&User name");
    private readonly TextBox _user = new() { MinimumSize = new Size(280, 0) };
    private readonly Label _passwordLabel = FieldLabel("&Password");
    private readonly TextBox _password = new() { MinimumSize = new Size(280, 0), UseSystemPasswordChar = true };
    private readonly CheckBox _trustCertificate = new() { Text = "&Trust the server's certificate", AutoSize = true };
    private readonly Button _test = ActionButton("Test &connection");
    private readonly StatusView _testStatus = new();

    // Shared folders
    private readonly DataGridView _folders = new();
    private readonly Button _addFolder = ActionButton("&Add folder…");
    private readonly Button _removeFolder = ActionButton("&Remove folder");

    // Connect Claude
    private readonly CheckBox _useInClaude = new()
    {
        Text = "Use &MakinoMCP in Claude on this PC",
        AutoSize = true,
        AutoCheck = false, // the box shows what Claude has, set after each change, never just the click
        Enabled = false,   // until the first check has run
        Margin = new Padding(0, 8, 0, 0),
        AccessibleDescription = "On adds MakinoMCP to Claude for your Windows account on this PC. Off removes it and stops it.",
    };
    private readonly StatusView _claudeStatus = new();
    private readonly Label _claudeNote = Wrapping("");
    private SwitchFacts? _switchFacts;
    private bool _switchBusy;
    private readonly Label _connectHint = Wrapping("Save first: the commands that connect Claude to MakinoMCP appear here.");
    private readonly TableLayoutPanel _snippets = new();
    private readonly TextBox _claudeCode = SnippetBox("Claude Code command");
    private readonly TextBox _cowork = SnippetBox("Cowork entry for claude_desktop_config.json");
    private readonly Button _copyClaudeCode = CopyButton("Copy the Claude Code command");
    private readonly Button _copyCowork = CopyButton("Copy the Cowork entry");

    // Bottom bar
    private readonly StatusView _saveStatus = new();
    private readonly Button _save = ActionButton("Sa&ve");
    private readonly Button _close = ActionButton("Close");

    public SetupForm(string settingsPath)
    {
        _settingsPath = Path.GetFullPath(settingsPath);

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = SystemFonts.MessageBoxFont ?? Font;
        Text = Title;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScroll = true;
        ClientSize = new Size(660, 760);
        MinimumSize = new Size(520, 360);
        CancelButton = _close; // Esc presses Close

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Padding = new Padding(20, 12, 20, 16),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        Add(root, Wrapping("MakinoMCP lets Claude read what the Makino cell is doing. It only ever reads: it never changes the cell database or the files in these folders."));
        BuildCellDatabase(root);
        BuildSharedFolders(root);
        BuildConnectClaude(root);
        BuildBottomBar(root);

        Controls.Add(root);
        ResumeLayout(false);
        PerformLayout();

        _windowsLogin.CheckedChanged += (_, _) => UpdateLoginFields();
        _test.Click += OnTestClick;
        _addFolder.Click += OnAddFolderClick;
        _removeFolder.Click += (_, _) => RemoveCurrentFolder();
        _copyClaudeCode.Click += (_, _) => Copy(_claudeCode, _copyClaudeCode);
        _copyCowork.Click += (_, _) => Copy(_cowork, _copyCowork);
        _save.Click += OnSaveClick;
        _useInClaude.Click += OnUseInClaudeClick;
        // Close() rather than DialogResult: the window runs modeless (Application.Run), where DialogResult closes nothing.
        _close.Click += (_, _) => Close();

        LoadExisting();
        WatchForChanges();
    }

    // ------------------------------------------------------------------ layout

    private void BuildCellDatabase(TableLayoutPanel root)
    {
        Add(root, Heading("Cell database"));
        Add(root, Wrapping("The SQL Server login MakinoMCP uses to read the cell. A login that can only read (db_datareader) is safest."));

        var t = Grid2();
        AddField(t, FieldLabel("&Server"), _server, "The cell PC's SQL Server, for example CELL-PC or CELL-PC\\SQLEXPRESS");
        AddField(t, FieldLabel("&Database"), _database, "The cell's database name");
        AddWide(t, _windowsLogin);
        AddField(t, _userLabel, _user, "SQL Server login name");
        AddField(t, _passwordLabel, _password, "SQL Server login password");
        AddWide(t, _trustCertificate);
        _test.Margin = new Padding(3, 10, 3, 3);
        AddWide(t, _test);
        Add(root, t);

        _testStatus.Margin = new Padding(0, 6, 0, 0);
        Add(root, _testStatus);
    }

    private void BuildSharedFolders(TableLayoutPanel root)
    {
        Add(root, Heading("Shared folders"));
        Add(root, Wrapping("Claude can read the files in these folders and their subfolders, for example the cell PC's logs or the ADE folder. Give each a short name; the description tells Claude what is in it."));

        _folders.Name = "folders";
        _folders.AccessibleName = "Shared folders";
        _folders.AccessibleDescription = "Name and description can be edited: press F2 or start typing.";
        _folders.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _folders.Height = 156;
        _folders.Margin = new Padding(0, 8, 0, 0);
        _folders.AllowUserToAddRows = false;
        _folders.AllowUserToDeleteRows = false;
        _folders.AllowUserToResizeRows = false;
        _folders.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells; // rows follow the font across DPI changes
        _folders.RowHeadersVisible = false;
        _folders.MultiSelect = false;
        _folders.StandardTab = true;
        _folders.EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2;
        _folders.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _folders.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        _folders.BackgroundColor = SystemColors.Window;
        // Plain headers take the system colours, so high contrast themes apply.
        _folders.EnableHeadersVisualStyles = false;
        _folders.ColumnHeadersDefaultCellStyle.BackColor = SystemColors.Control;
        _folders.ColumnHeadersDefaultCellStyle.ForeColor = SystemColors.ControlText;
        _folders.ColumnHeadersDefaultCellStyle.SelectionBackColor = SystemColors.Control;
        _folders.ColumnHeadersDefaultCellStyle.SelectionForeColor = SystemColors.ControlText;
        // Take whichever system text colour reads best on the selection, so high contrast themes keep a readable pair.
        // Gridlines likewise.
        _folders.DefaultCellStyle.SelectionBackColor = SystemColors.Highlight;
        _folders.DefaultCellStyle.SelectionForeColor =
            SetupChecks.MostContrasting(SystemColors.Highlight, SystemColors.HighlightText, SystemColors.WindowText);
        _folders.GridColor = SetupChecks.MostContrasting(SystemColors.Window,
            SystemColors.WindowFrame, SystemColors.ControlDarkDark, SystemColors.ControlDark);
        _folders.BorderStyle = BorderStyle.FixedSingle;
        _folders.Columns.Add(new DataGridViewTextBoxColumn { Name = "name", HeaderText = "Name", FillWeight = 22, MaxInputLength = 64 });
        _folders.Columns.Add(new DataGridViewTextBoxColumn { Name = "path", HeaderText = "Folder", FillWeight = 43, ReadOnly = true });
        _folders.Columns.Add(new DataGridViewTextBoxColumn { Name = "description", HeaderText = "Description", FillWeight = 35, MaxInputLength = 300 });
        foreach (DataGridViewColumn c in _folders.Columns) c.SortMode = DataGridViewColumnSortMode.NotSortable;
        NextTab(_folders);
        Add(root, _folders);

        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 6, 0, 0) };
        buttons.Controls.Add(_addFolder);
        buttons.Controls.Add(_removeFolder);
        NextTab(_addFolder);
        NextTab(_removeFolder);
        Add(root, buttons);
    }

    private void BuildConnectClaude(TableLayoutPanel root)
    {
        Add(root, Heading("Connect Claude"));
        Add(root, _useInClaude);
        _claudeStatus.Margin = new Padding(0, 6, 0, 0);
        Add(root, _claudeStatus);
        _claudeNote.Visible = false;
        _claudeNote.Margin = new Padding(0, 6, 0, 0);
        Add(root, _claudeNote);
        _connectHint.Margin = new Padding(0, 14, 0, 0);
        Add(root, _connectHint);

        _snippets.ColumnCount = 2;
        _snippets.AutoSize = true;
        _snippets.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _snippets.Margin = new Padding(0, 10, 0, 0);
        _snippets.Visible = false;
        _snippets.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _snippets.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        AddSnippet(_snippets, "Claude Code", _claudeCode, _copyClaudeCode);
        AddSnippet(_snippets, "Cowork (Claude desktop, not yet tested)", _cowork, _copyCowork);
        var how = Wrapping("Claude Code (tested): paste in a terminal. Cowork (not yet tested): add to the mcpServers section of " +
                           "claude_desktop_config.json, then restart Claude. If Cowork doesn't list the MakinoMCP tools after a " +
                           "restart, use Claude Code or open an issue on GitHub.");
        how.Margin = new Padding(0, 8, 0, 0);
        _snippets.Controls.Add(how);
        _snippets.SetColumnSpan(how, 2);
        Add(root, _snippets);
    }

    private void BuildBottomBar(TableLayoutPanel root)
    {
        _saveStatus.Margin = new Padding(0, 20, 0, 0);
        Add(root, _saveStatus);

        var bar = new TableLayoutPanel
        {
            ColumnCount = 3,
            AutoSize = true,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Margin = new Padding(0, 12, 0, 0),
        };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var where = Wrapping($"Saves to {_settingsPath}");
        where.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        bar.Controls.Add(where);
        bar.Controls.Add(_save);
        bar.Controls.Add(_close);
        NextTab(_save);
        NextTab(_close);
        Add(root, bar);
    }

    private void Add(TableLayoutPanel root, Control c)
    {
        root.Controls.Add(c);
        c.TabIndex = _tabIndex++;
    }

    private void NextTab(Control c) => c.TabIndex = _tabIndex++;

    private static TableLayoutPanel Grid2()
    {
        var t = new TableLayoutPanel
        {
            ColumnCount = 2,
            AutoSize = true,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Margin = new Padding(0, 8, 0, 0),
        };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return t;
    }

    // A label's mnemonic focuses the next control in tab order, so each label takes the tab stop just before its field.
    private void AddField(TableLayoutPanel t, Label label, TextBox box, string description)
    {
        box.AccessibleName = label.Text.Replace("&", "", StringComparison.Ordinal);
        box.AccessibleDescription = description;
        box.Anchor = AnchorStyles.Left;
        label.TabIndex = _tabIndex++;
        box.TabIndex = _tabIndex++;
        t.Controls.Add(label);
        t.Controls.Add(box);
    }

    private void AddWide(TableLayoutPanel t, Control c)
    {
        c.TabIndex = _tabIndex++;
        t.Controls.Add(new Label { AutoSize = true, Margin = Padding.Empty, TabStop = false });
        t.Controls.Add(c);
    }

    private void AddSnippet(TableLayoutPanel t, string caption, TextBox box, Button copy)
    {
        var label = new Label { Text = caption, AutoSize = true, Margin = new Padding(0, 8, 0, 2), UseMnemonic = false };
        t.Controls.Add(label);
        t.SetColumnSpan(label, 2);
        box.TabIndex = _tabIndex++;
        copy.TabIndex = _tabIndex++;
        t.Controls.Add(box);
        t.Controls.Add(copy);
    }

    private static Label Heading(string text)
    {
        var baseFont = SystemFonts.MessageBoxFont ?? Control.DefaultFont;
        return new Label
        {
            Text = text,
            AutoSize = true,
            UseMnemonic = false,
            Font = new Font(baseFont.FontFamily, baseFont.SizeInPoints * 1.2F, FontStyle.Bold, GraphicsUnit.Point),
            Margin = new Padding(0, 22, 0, 2),
        };
    }

    private static Label Wrapping(string text) => new()
    {
        Text = text,
        AutoSize = true,
        UseMnemonic = false,
        Anchor = AnchorStyles.Left | AnchorStyles.Right,
        Margin = new Padding(0, 2, 0, 0),
    };

    private static Label FieldLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(0, 6, 12, 6),
    };

    private static TextBox SnippetBox(string name)
    {
        var baseFont = SystemFonts.MessageBoxFont ?? Control.DefaultFont;
        return new TextBox
        {
            ReadOnly = true,
            Multiline = true,
            WordWrap = true,
            Height = 58, // three lines of the snippet font
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            AccessibleName = name,
            Font = new Font("Consolas", baseFont.SizeInPoints, FontStyle.Regular, GraphicsUnit.Point),
        };
    }

    // GrowAndShrink: with the default GrowOnly a button keeps a default size that DPI scaling has already enlarged.
    private static Button ActionButton(string text) => new()
    {
        Text = text,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Padding = new Padding(10, 2, 10, 2),
        MinimumSize = new Size(88, 0),
    };

    private static Button CopyButton(string name)
    {
        var b = ActionButton("Copy");
        b.AccessibleName = name;
        b.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        return b;
    }

    // ------------------------------------------------------------------ load and state

    private void LoadExisting()
    {
        _trustCertificate.Checked = true;
        if (!File.Exists(_settingsPath))
        {
            UpdateLoginFields();
            return;
        }

        var load = SettingsStore.Load(_settingsPath);
        if (load.Settings is not { } s)
        {
            _saveStatus.Set(new StatusLine(StatusKind.Warning, $"{load.Problem} Saving replaces it."));
            UpdateLoginFields();
            return;
        }

        _limits = s.Limits;
        _connectTimeoutSeconds = s.Makino.ConnectTimeoutSeconds;
        _server.Text = s.Makino.Server;
        _database.Text = s.Makino.Database;
        _windowsLogin.Checked = s.Makino.UseWindowsLogin;
        _user.Text = s.Makino.User;
        _password.Text = s.Makino.Password;
        _trustCertificate.Checked = s.Makino.TrustServerCertificate;
        foreach (var f in s.Folders) _folders.Rows.Add(f.Name, f.Path, f.Description);
        UpdateLoginFields();
        ShowSnippets();
    }

    private void WatchForChanges()
    {
        void Dirty(object? sender, EventArgs e) => _dirty = true;
        foreach (var box in new[] { _server, _database, _user, _password }) box.TextChanged += Dirty;
        _windowsLogin.CheckedChanged += Dirty;
        _trustCertificate.CheckedChanged += Dirty;
        _folders.CellValueChanged += Dirty;
        _folders.RowsAdded += Dirty;
        _folders.RowsRemoved += Dirty;
        _dirty = false;
    }

    private void UpdateLoginFields()
    {
        var sql = !_windowsLogin.Checked;
        _userLabel.Enabled = _user.Enabled = sql;
        _passwordLabel.Enabled = _password.Enabled = sql;
    }

    private MakinoLogin CurrentLogin() => _windowsLogin.Checked
        ? new MakinoLogin(_server.Text.Trim(), _database.Text.Trim(), true, "", "", _trustCertificate.Checked, _connectTimeoutSeconds)
        : new MakinoLogin(_server.Text.Trim(), _database.Text.Trim(), false, _user.Text.Trim(), _password.Text,
            _trustCertificate.Checked, _connectTimeoutSeconds);

    private List<SharedFolder> CurrentFolders() => _folders.Rows.Cast<DataGridViewRow>()
        .Select(r => new SharedFolder(Cell(r, "name").Trim(), Cell(r, "path"), Cell(r, "description").Trim()))
        .ToList();

    private static string Cell(DataGridViewRow r, string column) => r.Cells[column].Value as string ?? "";

    /// <summary>Puts the caret in the first empty login field, so a validation message leads straight to the fix.</summary>
    private void FocusFirstMissing()
    {
        if (string.IsNullOrWhiteSpace(_server.Text)) _server.Focus();
        else if (string.IsNullOrWhiteSpace(_database.Text)) _database.Focus();
        else if (!_windowsLogin.Checked && string.IsNullOrWhiteSpace(_user.Text)) _user.Focus();
    }

    // ------------------------------------------------------------------ test connection

    private async void OnTestClick(object? sender, EventArgs e)
    {
        if (SetupChecks.ValidateLogin(_server.Text, _database.Text, _windowsLogin.Checked, _user.Text) is { } missing)
        {
            _testStatus.Set(new StatusLine(StatusKind.Error, missing));
            FocusFirstMissing();
            return;
        }

        var login = CurrentLogin();
        _test.Enabled = false;
        var width = _test.MinimumSize;
        _test.MinimumSize = _test.Size; // keep its place while the label is shorter
        _test.Text = "Testing…";
        _testStatus.Set(new StatusLine(StatusKind.Info, $"Testing the connection to {login.Server}…"));
        StatusLine result;
        try
        {
            result = await SetupChecks.TestConnectionAsync(new SqlMakinoReader(login, _limits), login, TestCap, _closing.Token);
        }
        catch (Exception ex)
        {
            result = new StatusLine(StatusKind.Error, ErrorText.ForClaude(ex, login));
        }
        if (IsDisposed || _closing.IsCancellationRequested) return;

        _test.Text = "Test &connection";
        _test.MinimumSize = width;
        _test.Enabled = true;
        _testStatus.Set(result);
    }

    // ------------------------------------------------------------------ folders

    private void OnAddFolderClick(object? sender, EventArgs e)
    {
        using var pick = new FolderBrowserDialog
        {
            Description = "Choose a folder Claude may read",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
        };
        if (pick.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(pick.SelectedPath)) return;

        // Other PCs may map the share to another letter, so keep the UNC path.
        var path = UncPath.FromMapped(pick.SelectedPath, UncPath.DriveToUnc);
        var folders = CurrentFolders();
        var same = folders.FindIndex(f => string.Equals(f.Path.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        if (same >= 0)
        {
            _folders.CurrentCell = _folders.Rows[same].Cells["name"];
            _saveStatus.Set(new StatusLine(StatusKind.Info, $"That folder is already in the list as '{folders[same].Name}'."));
            return;
        }

        var name = SetupChecks.UniqueName(path, folders.Select(f => f.Name));
        var row = _folders.Rows.Add(name, path, "");
        _folders.Focus();
        _folders.CurrentCell = _folders.Rows[row].Cells["description"];
        _folders.BeginEdit(selectAll: true);
    }

    private void RemoveCurrentFolder()
    {
        if (_folders.CurrentRow is not { } row) return;
        var index = row.Index;
        _folders.Rows.RemoveAt(index);
        if (_folders.Rows.Count > 0)
            _folders.CurrentCell = _folders.Rows[Math.Min(index, _folders.Rows.Count - 1)].Cells["name"];
    }

    // ------------------------------------------------------------------ save

    private async void OnSaveClick(object? sender, EventArgs e)
    {
        _folders.EndEdit();
        if (SetupChecks.ValidateLogin(_server.Text, _database.Text, _windowsLogin.Checked, _user.Text) is { } missing)
        {
            _saveStatus.Set(new StatusLine(StatusKind.Error, $"Not saved. {missing}"));
            FocusFirstMissing();
            return;
        }
        var folders = CurrentFolders();
        if (SetupChecks.ValidateFolders(folders) is { } folderProblem)
        {
            _saveStatus.Set(new StatusLine(StatusKind.Error, $"Not saved. {folderProblem}"));
            _folders.Focus();
            return;
        }

        try
        {
            SettingsStore.Save(_settingsPath, new McpSettings(CurrentLogin(), folders, _limits));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
            or ArgumentException or System.Security.SecurityException)
        {
            _saveStatus.Set(new StatusLine(StatusKind.Error, SetupChecks.SaveError(_settingsPath, ex)));
            return;
        }
        _dirty = false;
        ShowSnippets();
        if (_switchFacts is { } facts && !_switchBusy) ShowSwitch(facts with { SettingsSaved = true });

        _save.Enabled = false;
        _saveStatus.Set(new StatusLine(StatusKind.Info, "Saved. Checking the folders…"));
        var unreachable = await Task.Run(() => SetupChecks.Unreachable(folders, FolderProbeCap));
        if (IsDisposed || _closing.IsCancellationRequested) return;
        _save.Enabled = true;
        _saveStatus.Set(unreachable.Count == 0
            ? new StatusLine(StatusKind.Success, $"Saved. {NextStart}")
            : new StatusLine(StatusKind.Warning,
                $"Saved. {NextStart} This PC can't reach {string.Join(", ", unreachable.Select(n => $"'{n}'"))}: Claude can't read {(unreachable.Count == 1 ? "it" : "them")} until it can."));
    }

    /// <summary>The launcher Claude should start: beside the settings file, by its UNC path when on a mapped drive.</summary>
    private string Launcher() => UncPath.FromMapped(InstallSnippets.LauncherPath(_settingsPath), UncPath.DriveToUnc);

    private void ShowSnippets()
    {
        var (claudeCode, cowork) = InstallSnippets.For(Launcher());
        _claudeCode.Text = claudeCode;
        _cowork.Text = cowork;
        _connectHint.Visible = false;
        _snippets.Visible = true;
    }

    private void Copy(TextBox source, Button button)
    {
        try
        {
            Clipboard.SetDataObject(source.Text, copy: true, retryTimes: 5, retryDelay: 100);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            _saveStatus.Set(new StatusLine(StatusKind.Error, "Couldn't copy: another program is using the clipboard. Try again."));
            return;
        }

        // The button says what happened, then goes back.
        button.Text = "Copied";
        button.AccessibilityObject.RaiseAutomationNotification(
            System.Windows.Forms.Automation.AutomationNotificationKind.ActionCompleted,
            System.Windows.Forms.Automation.AutomationNotificationProcessing.ImportantMostRecent,
            "Copied to the clipboard");
        var timer = new System.Windows.Forms.Timer { Interval = (int)CopiedFor.TotalMilliseconds };
        timer.Tick += (_, _) =>
        {
            timer.Dispose();
            if (!button.IsDisposed) button.Text = "Copy";
        };
        timer.Start();
    }

    // ------------------------------------------------------------------ use in Claude on this PC

    private static readonly string ClaudeWorkingFolder = Path.Combine(Path.GetTempPath(), "MakinoMCP-claude");

    /// <summary>Reads what Claude Code and Cowork have now, and makes the switch that changes them. Off the UI
    /// thread: claude can take seconds to answer.</summary>
    private Task<(SwitchFacts Facts, ClaudeSwitch Switch)> ReadSwitchAsync(CancellationToken ct)
    {
        var launcher = Launcher();
        var saved = File.Exists(_settingsPath);
        return Task.Run(async () =>
        {
            // An empty folder of its own: no project there whose local or .mcp.json entry could hide the user one.
            Directory.CreateDirectory(ClaudeWorkingFolder);
            var runner = new ProcessCommandRunner(ClaudeWorkingFolder);
            var claudePath = await ClaudeRegistration.FindAsync(runner, ct);
            var registration = claudePath is null ? null : new ClaudeRegistration(runner, claudePath);
            var status = registration is null ? null : await registration.GetStatusAsync(ct);
            var cowork = CoworkConfig.FilesForThisUser();
            var facts = new SwitchFacts(status, cowork.Count > 0, CoworkConfig.AnyHasEntry(cowork), saved, launcher);
            return (facts, new ClaudeSwitch(registration, cowork, RunningServers.StopForThisUser, () => DateTime.Now));
        }, ct);
    }

    /// <summary>Reads the state afresh and shows it; <paramref name="result"/>, when given, replaces the status line
    /// (the outcome of a change says more than the state alone).</summary>
    private async Task RefreshSwitchAsync(StatusLine? result = null)
    {
        SetSwitchBusy(true);
        if (result is null) _claudeStatus.Set(new StatusLine(StatusKind.Info, "Checking whether Claude uses MakinoMCP…"));
        SwitchFacts facts;
        try
        {
            (facts, _) = await ReadSwitchAsync(_closing.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) // never a crash or a stack trace: the message in plain words
        {
            facts = new SwitchFacts(new ClaudeStatus(RegistrationState.Unknown, Problem: ex.Message), false, false,
                File.Exists(_settingsPath), Launcher());
        }
        if (IsDisposed || _closing.IsCancellationRequested) return;

        SetSwitchBusy(false);
        // Saved now, not when the read began: Save may have been pressed while claude was answering.
        ShowSwitch(facts with { SettingsSaved = File.Exists(_settingsPath) }, result);
    }

    /// <summary>While busy the box stays enabled, so the keyboard focus stays on it (disabling would move the focus
    /// away mid-change); clicks are ignored and the status line says what is happening.</summary>
    private void SetSwitchBusy(bool busy)
    {
        _switchBusy = busy;
        _useInClaude.Cursor = busy ? Cursors.AppStarting : Cursors.Default;
    }

    private void ShowSwitch(SwitchFacts facts, StatusLine? result = null)
    {
        _switchFacts = facts;
        var view = ClaudeSwitch.Describe(facts);
        _useInClaude.Checked = view.Checked;
        _useInClaude.Enabled = view.Enabled;
        _claudeStatus.Set(result ?? view.Status);
        _claudeNote.Text = view.Note ?? "";
        _claudeNote.Visible = view.Note is not null;
    }

    private async void OnUseInClaudeClick(object? sender, EventArgs e)
    {
        if (_switchBusy || !_useInClaude.Enabled) return;
        var turnOn = !_useInClaude.Checked;
        if (!turnOn)
        {
            var answer = MessageBox.Show(this,
                "Turn off MakinoMCP for Claude on this PC? Claude sessions that are open will lose the MakinoMCP tools.",
                Title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;
        }

        SetSwitchBusy(true);
        _claudeStatus.Set(new StatusLine(StatusKind.Info, turnOn ? "Turning on…" : "Turning off and stopping MakinoMCP…"));
        StatusLine result;
        try
        {
            // A fresh switch: Claude Code may have been installed or changed since the window opened.
            var (_, sw) = await ReadSwitchAsync(_closing.Token);
            var launcher = Launcher();
            var ct = _closing.Token;
            // Off the UI thread, starting the claude processes included.
            result = await Task.Run(() => turnOn ? sw.TurnOnAsync(launcher, ct) : sw.TurnOffAsync(ct), ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) // never a crash or a stack trace: the message in plain words
        {
            result = new StatusLine(StatusKind.Error, $"Couldn't turn {(turnOn ? "on" : "off")} MakinoMCP for Claude: {ex.Message}");
        }
        if (IsDisposed || _closing.IsCancellationRequested) return;

        await RefreshSwitchAsync(result);
    }

    // ------------------------------------------------------------------ opening and closing

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        await RefreshSwitchAsync();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // Scaling has run by now: keep the whole window on its screen (at 150-200% the full height may not fit) and let
        // AutoScroll show the rest.
        var area = Screen.FromControl(this).WorkingArea;
        var size = new Size(Math.Min(Width, area.Width), Math.Min(Height, area.Height));
        if (size == Size) return;
        Size = size;
        Location = new Point(area.Left + (area.Width - size.Width) / 2, area.Top + (area.Height - size.Height) / 2);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_dirty && e.CloseReason == CloseReason.UserClosing)
        {
            var answer = MessageBox.Show(this, "Your changes aren't saved. Close without saving them?", Title,
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _closing.Cancel();
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _closing.Dispose();
        base.Dispose(disposing);
    }

    /// <summary>An icon plus words. The icon is a glyph from the Windows icon font drawn in the inherited text font's
    /// size and colour, so it follows display scaling and high contrast; it and its accessible name only
    /// repeat what the words already say. Screen readers hear each new line.</summary>
    private sealed class StatusView : TableLayoutPanel
    {
        private readonly Glyph _icon = new() { Margin = new Padding(0, 0, 6, 0) };

        private readonly Label _text = new()
        {
            AutoSize = true,
            UseMnemonic = false,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Margin = Padding.Empty,
        };

        public StatusView()
        {
            ColumnCount = 2;
            AutoSize = true;
            Anchor = AnchorStyles.Left | AnchorStyles.Right;
            Visible = false;
            TabStop = false;
            ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Controls.Add(_icon);
            Controls.Add(_text);
        }

        public void Set(StatusLine line)
        {
            var (glyph, word) = line.Kind switch
            {
                StatusKind.Success => ("\uE930", "Done"),        // Completed: a ticked circle
                StatusKind.Warning => ("\uE7BA", "Warning"),     // Warning: a triangle with !
                StatusKind.Error => ("\uEA39", "Error"),         // ErrorBadge: a crossed circle
                _ => ("\uE946", "Information"),                  // Info: a circle with i
            };
            _icon.Text = glyph;
            _icon.AccessibleName = word;
            _text.Text = line.Text;
            AccessibleName = $"{word}: {line.Text}";
            Visible = true;
            _text.AccessibilityObject.RaiseAutomationNotification(
                System.Windows.Forms.Automation.AutomationNotificationKind.Other,
                System.Windows.Forms.Automation.AutomationNotificationProcessing.ImportantMostRecent,
                line.Text);
        }
    }

    /// <summary>One icon-font glyph. The font is never set on the control (WinForms would rescale it again on a DPI
    /// change); it is derived from the inherited font each time it is measured or drawn.</summary>
    private sealed class Glyph : Control
    {
        private static readonly string IconFont =
            FontFamily.Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

        private const TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;

        public Glyph()
        {
            AutoSize = true;
            TabStop = false;
            AccessibleRole = AccessibleRole.Graphic;
            SetStyle(ControlStyles.Selectable, false);
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        private Font IconFontNow() => new(IconFont, Font.Size, FontStyle.Regular, Font.Unit);

        public override Size GetPreferredSize(Size proposedSize)
        {
            using var f = IconFontNow();
            var glyph = TextRenderer.MeasureText(Text.Length == 0 ? "\uE946" : Text, f, Size.Empty, Flags);
            // As tall as a line of the text beside it, so the two sit on one baseline band.
            return new Size(glyph.Width, Math.Max(glyph.Height, Font.Height));
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            Parent?.PerformLayout(this, nameof(Text));
            Invalidate();
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            Parent?.PerformLayout(this, nameof(Font));
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            using var f = IconFontNow();
            TextRenderer.DrawText(e.Graphics, Text, f, ClientRectangle, ForeColor,
                Flags | TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
        }
    }
}
