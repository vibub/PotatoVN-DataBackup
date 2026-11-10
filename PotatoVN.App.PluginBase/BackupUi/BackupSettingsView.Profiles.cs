using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PotatoVN.Backup.Core;

namespace PotatoVN.App.PluginBase.BackupUi;

public sealed partial class BackupSettingsView
{
    private readonly ComboBox _profilePick = new() { Header = "选择渠道配置", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _providerKind = new() { Header = "渠道类型", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _profileName = TextInput("配置名称", "例如：家中 NAS");
    private readonly TextBox _endpoint = TextInput("WebDAV 地址", "https://example.com/dav/");
    private readonly TextBox _username = TextInput("用户名");
    private readonly PasswordBox _password = new() { Header = "密码或应用专用密码", PlaceholderText = "留空保留已保存密码" };
    private readonly TextBox _remotePath = TextInput("备份目录", "留空使用服务器根目录");
    private readonly TextBox _clientId = TextInput("OAuth 应用 ID");
    private readonly TextBox _tenant = TextInput("微软登录目录", "通常使用 common");
    private readonly TextBox _googleJson = new()
    {
        Header = "谷歌桌面应用配置", PlaceholderText = "粘贴从 Google Cloud 下载的桌面应用 JSON",
        AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 96, MaxHeight = 180,

    };
    private StackPanel _webDavFields = null!;
    private StackPanel _oauthFields = null!;
    private StackPanel _googleFields = null!;
    private ProviderProfile? _editingProfile;

    private StackPanel BuildProfilesTab()
    {
        var panel = Stack();
        panel.Children.Add(Heading("备份渠道"));
        panel.Children.Add(Note("可以保存多个配置，每次备份使用当前渠道。首次使用微软或谷歌云盘，需要注册自己的 OAuth 应用。"));
        panel.Children.Add(_profilePick);
        var profileActions = Row();
        profileActions.Children.Add(ActionButton("新增渠道", async _ =>
        {
            var profile = new ProviderProfile { Name = $"渠道 {_controller.Settings.Profiles.Count + 1}" };
            _controller.Settings.Profiles.Add(profile);
            _controller.Settings.ActiveProfileId ??= profile.Id;
            await _controller.SaveSettingsAsync();
            RefreshProfiles(profile.Id);
        }));
        profileActions.Children.Add(ActionButton("设为当前", async _ =>
        {
            await SaveProfileFieldsAsync();
            _controller.Settings.ActiveProfileId = RequireProfile().Id;
            await _controller.SaveSettingsAsync();
            _snapshotList.Items.Clear();
        }));
        profileActions.Children.Add(ActionButton("删除配置", async _ =>
        {
            ProviderProfile profile = RequireProfile();
            if (await DialogAsync("删除渠道配置", Note($"删除“{profile.Name}”的本机配置？云端备份不会删除。"), "删除配置") != ContentDialogResult.Primary) return;
            _controller.Settings.Profiles.Remove(profile);
            if (_controller.Settings.ActiveProfileId == profile.Id)
                _controller.Settings.ActiveProfileId = _controller.Settings.Profiles.FirstOrDefault()?.Id;
            await _controller.SaveSettingsAsync();
            _editingProfile = null;
            RefreshProfiles(_controller.Settings.ActiveProfileId);
            _snapshotList.Items.Clear();
        }));
        panel.Children.Add(profileActions);
        panel.Children.Add(_profileName);
        AddChoice(_providerKind, ProviderKind.WebDav, "WebDAV");
        AddChoice(_providerKind, ProviderKind.OneDrive, "OneDrive");
        AddChoice(_providerKind, ProviderKind.GoogleDrive, "Google Drive");
        panel.Children.Add(_providerKind);
        _webDavFields = Stack();
        foreach (UIElement field in new UIElement[] { _endpoint, _username, _password, _remotePath }) _webDavFields.Children.Add(field);
        panel.Children.Add(_webDavFields);
        _oauthFields = Stack();
        _oauthFields.Children.Add(_clientId);
        _oauthFields.Children.Add(_tenant);
        _oauthFields.Children.Add(Note("授权会打开系统浏览器。令牌保存在本机；新设备请使用相同的应用配置和云盘账号。"));
        panel.Children.Add(_oauthFields);
        _googleFields = Stack();
        _googleFields.Children.Add(_googleJson);
        _googleFields.Children.Add(ActionButton("导入粘贴的谷歌配置", async _ =>
        {
            ProviderProfile profile = RequireProfile();
            using JsonDocument document = JsonDocument.Parse(_googleJson.Text);
            if (!document.RootElement.TryGetProperty("installed", out JsonElement installed) ||
                !installed.TryGetProperty("client_id", out JsonElement id) || string.IsNullOrWhiteSpace(id.GetString()))
                throw new InvalidOperationException("请粘贴 Google Cloud 中“桌面应用”类型的 OAuth 配置 JSON。");
            profile.Kind = ProviderKind.GoogleDrive;
            profile.ClientId = id.GetString()!;
            if (installed.TryGetProperty("client_secret", out JsonElement secret))
                await _controller.SetSecretAsync(profile.Id, "client-secret", secret.GetString() ?? "");
            _clientId.Text = profile.ClientId;
            SelectChoice(_providerKind, ProviderKind.GoogleDrive);
            _googleJson.Text = "";
            await SaveProfileFieldsAsync();
        }));
        panel.Children.Add(_googleFields);
        var connectionActions = Row();
        connectionActions.Children.Add(ActionButton("保存渠道", async _ => await SaveProfileFieldsAsync()));
        connectionActions.Children.Add(ActionButton("登录 / 重新授权", async ct =>
        {
            await SaveProfileFieldsAsync();
            ProviderProfile profile = RequireProfile();
            if (profile.Kind == ProviderKind.WebDav) throw new InvalidOperationException("WebDAV 使用账号密码，无需浏览器授权，请点击测试连接。");
            await _controller.AuthorizeAsync(profile.Id, ct);
        }));
        connectionActions.Children.Add(ActionButton("测试连接", async ct =>
        {
            await SaveProfileFieldsAsync();
            await _controller.ProbeAsync(RequireProfile().Id, ct);
        }));
        panel.Children.Add(connectionActions);
        panel.Children.Add(Note("删除本机配置不会删除云端文件。备份使用自己的云盘空间；压缩包不额外加密。"));
        _profilePick.SelectionChanged += (_, _) =>
        {
            if (!_loading) LoadProfile(Choice(_profilePick) is Guid id ? _controller.Settings.Profiles.FirstOrDefault(p => p.Id == id) : null);
        };
        _providerKind.SelectionChanged += (_, _) => UpdateProviderFields();
        RefreshProfiles(_controller.Settings.ActiveProfileId);
        return panel;
    }

    private void RefreshProfiles(Guid? selected)
    {
        _loading = true;
        _profilePick.Items.Clear();
        foreach (ProviderProfile profile in _controller.Settings.Profiles)
            AddChoice(_profilePick, profile.Id, $"{profile.Name} · {ProviderLabel(profile.Kind)}");
        if (selected is Guid id) SelectChoice(_profilePick, id);
        if (_profilePick.SelectedItem == null && _profilePick.Items.Count > 0) _profilePick.SelectedIndex = 0;
        _loading = false;
        LoadProfile(Choice(_profilePick) is Guid pick ? _controller.Settings.Profiles.FirstOrDefault(p => p.Id == pick) : null);
        RefreshStatus();
    }

    private void LoadProfile(ProviderProfile? profile)
    {
        _editingProfile = profile;
        _loading = true;
        _profileName.Text = profile?.Name ?? "";
        SelectChoice(_providerKind, profile?.Kind ?? ProviderKind.WebDav);
        _endpoint.Text = profile?.Endpoint ?? "";
        _username.Text = profile?.Username ?? "";
        _remotePath.Text = profile?.RemotePath ?? "";
        _clientId.Text = profile?.ClientId ?? "";
        _tenant.Text = profile?.Tenant ?? "common";
        _password.Password = "";
        _googleJson.Text = "";
        _loading = false;
        UpdateProviderFields();
    }

    private void UpdateProviderFields()
    {
        ProviderKind kind = Choice(_providerKind) is ProviderKind value ? value : ProviderKind.WebDav;
        if (_webDavFields == null) return;
        _webDavFields.Visibility = kind == ProviderKind.WebDav ? Visibility.Visible : Visibility.Collapsed;
        _oauthFields.Visibility = kind == ProviderKind.WebDav ? Visibility.Collapsed : Visibility.Visible;
        _googleFields.Visibility = kind == ProviderKind.GoogleDrive ? Visibility.Visible : Visibility.Collapsed;
        _tenant.Visibility = kind == ProviderKind.OneDrive ? Visibility.Visible : Visibility.Collapsed;
    }

    private ProviderProfile RequireProfile() => _editingProfile ?? throw new InvalidOperationException("请先新增一个渠道配置。");

    private async Task SaveProfileFieldsAsync()
    {
        ProviderProfile profile = RequireProfile();
        if (string.IsNullOrWhiteSpace(_profileName.Text)) throw new InvalidOperationException("请填写渠道配置名称。");
        ProviderKind kind = Choice(_providerKind) is ProviderKind picked ? picked : ProviderKind.WebDav;
        if (kind == ProviderKind.WebDav && (!Uri.TryCreate(_endpoint.Text.Trim(), UriKind.Absolute, out Uri? endpoint) ||
            (endpoint.Scheme != Uri.UriSchemeHttps && endpoint.Scheme != Uri.UriSchemeHttp)))
            throw new InvalidOperationException("请填写以 https:// 或 http:// 开头的 WebDAV 地址。");
        profile.Name = _profileName.Text.Trim();
        profile.Kind = kind;
        profile.Endpoint = _endpoint.Text.Trim();
        profile.Username = _username.Text;
        profile.RemotePath = _remotePath.Text.Trim();
        profile.ClientId = _clientId.Text.Trim();
        profile.Tenant = string.IsNullOrWhiteSpace(_tenant.Text) ? "common" : _tenant.Text.Trim();
        if (!string.IsNullOrEmpty(_password.Password))
        {
            await _controller.SetSecretAsync(profile.Id, "password", _password.Password);
            _password.Password = "";
        }
        await _controller.SaveSettingsAsync();
        RefreshProfiles(profile.Id);
    }
}
