using System.ComponentModel;

namespace BsgoBot.Ui;

/// <summary>Edit the server and client lists. Grids so adding the tenth server is
/// as cheap as the second.</summary>
public sealed class ProfilesDialog : Form
{
    private readonly BindingList<ServerProfile> _servers;
    private readonly BindingList<ClientProfile> _clients;

    public ProfilesDialog(Config cfg)
    {
        Text = "Servers & Clients";
        Width = 940;
        Height = 580;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.Ui;
        HandleCreated += (_, _) => Theme.UseDarkTitleBar(this);

        _servers = new BindingList<ServerProfile>(cfg.Servers);
        _clients = new BindingList<ClientProfile>(cfg.Clients);

        var serverGrid = MakeGrid(_servers);
        var clientGrid = MakeGrid(_clients);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(MakeTab("Servers", serverGrid,
            "Host/Port is where the real game server listens. PlayerId and Session are the account used to launch the client.",
            () => _servers.Add(new ServerProfile()),
            () => RemoveCurrent(serverGrid, _servers)));
        tabs.TabPages.Add(MakeTab("Clients", clientGrid,
            "Path is the folder containing bsgo.exe. Version must match what the server reports.",
            () => _clients.Add(new ClientProfile()),
            () => RemoveCurrent(clientGrid, _clients),
            BrowseForClient));

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 46,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(8),
        };
        buttons.BackColor = Theme.Panel;
        var ok = new FlatButton { Text = "Save", Width = 92, Primary = true, DialogResult = DialogResult.OK };
        var cancel = new FlatButton { Text = "Cancel", Width = 92, DialogResult = DialogResult.Cancel };
        buttons.Controls.AddRange([ok, cancel]);

        AcceptButton = ok;
        CancelButton = cancel;

        Controls.Add(tabs);
        Controls.Add(buttons);
    }

    private static DataGridView MakeGrid<T>(BindingList<T> source)
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            DataSource = source,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            BackgroundColor = Theme.Card,
            ForeColor = Theme.Text,
            GridColor = Theme.Border,
            BorderStyle = BorderStyle.None,
            EnableHeadersVisualStyles = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            RowHeadersVisible = false,
            ColumnHeadersHeight = 32,
            Font = Theme.Mono,
        };

        // The grid ignores the form's palette entirely; every surface has to be set by hand.
        grid.ColumnHeadersDefaultCellStyle.BackColor = Theme.Panel;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Theme.Muted;
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Theme.Panel;
        grid.ColumnHeadersDefaultCellStyle.Font = Theme.Header;
        grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(6, 0, 0, 0);

        grid.DefaultCellStyle.BackColor = Theme.Card;
        grid.DefaultCellStyle.ForeColor = Theme.Text;
        grid.DefaultCellStyle.SelectionBackColor = Theme.AccentDeep;
        grid.DefaultCellStyle.SelectionForeColor = Theme.Text;
        grid.DefaultCellStyle.Padding = new Padding(6, 0, 0, 0);

        grid.AlternatingRowsDefaultCellStyle.BackColor = Theme.CardHi;
        grid.AlternatingRowsDefaultCellStyle.SelectionBackColor = Theme.AccentDeep;

        return grid;
    }

    private static TabPage MakeTab(string title, DataGridView grid, string hint,
        Action onAdd, Action onRemove, Action<DataGridView>? onBrowse = null)
    {
        var page = new TabPage(title) { BackColor = Theme.Bg, Padding = new Padding(6) };

        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, Height = 42, Padding = new Padding(2, 6, 2, 6), BackColor = Theme.Bg,
        };
        var add = new FlatButton { Text = "Add", Width = 80 };
        add.Click += (_, _) => onAdd();
        var del = new FlatButton { Text = "Remove", Width = 84 };
        del.Click += (_, _) => onRemove();
        bar.Controls.AddRange([add, del]);

        if (onBrowse is not null)
        {
            var browse = new FlatButton { Text = "Browse folder...", Width = 134 };
            browse.Click += (_, _) => onBrowse(grid);
            bar.Controls.Add(browse);
        }

        var note = new Label
        {
            Dock = DockStyle.Bottom, Height = 36, Text = hint,
            ForeColor = Theme.Faint, Font = Theme.UiSmall, Padding = new Padding(2, 6, 2, 2),
        };

        page.Controls.Add(grid);
        page.Controls.Add(note);
        page.Controls.Add(bar);
        return page;
    }

    private static void RemoveCurrent<T>(DataGridView grid, BindingList<T> list)
    {
        int i = grid.CurrentRow?.Index ?? -1;
        if (i >= 0 && i < list.Count) list.RemoveAt(i);
    }

    private void BrowseForClient(DataGridView grid)
    {
        if (grid.CurrentRow?.DataBoundItem is not ClientProfile profile)
        {
            MessageBox.Show("Select a client row first.");
            return;
        }

        using var dlg = new FolderBrowserDialog { Description = "Pick the folder containing bsgo.exe" };
        if (!string.IsNullOrWhiteSpace(profile.Path) && Directory.Exists(profile.Path))
            dlg.SelectedPath = profile.Path;

        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        if (!File.Exists(Path.Combine(dlg.SelectedPath, "bsgo.exe")))
        {
            var go = MessageBox.Show(
                "No bsgo.exe in that folder. Use it anyway?",
                "bsgo.exe not found", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (go != DialogResult.Yes) return;
        }

        profile.Path = dlg.SelectedPath;
        if (profile.Name is "New client" or "")
            profile.Name = new DirectoryInfo(dlg.SelectedPath).Name;
        grid.Refresh();
    }
}
