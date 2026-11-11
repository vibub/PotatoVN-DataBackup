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
    private readonly ComboBox _profilePick = new() { Header = "使用的备份渠道", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _providerKind = new() { Header = "备份到哪里", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _profileName = TextInput("渠道名称（可选）", "留空按云盘类型命名");
    private readonly TextBox _endpoint = TextInput("WebDAV 服务器地址", "例如：https://dav.jianguoyun.com/dav/");
    private readonly TextBox _username = TextInput("账号 / 用户名");
    private readonly PasswordBox _password = new() { Header = "密码或应用专用密码", PlaceholderText = "填写服务商提供的密码" };
    private readonly TextBox _clientId = TextInput("应用程序（客户端）ID", "粘贴 Microsoft Entra 中的应用 ID");
    private readonly TextBox _tenant = TextInput("微软登录目录", "通常无需修改，默认 common");
    private readonly TextBox _googleJson = new()
    {
        Header = "Google 桌面应用配置 JSON", PlaceholderText = "粘贴从 Google Cloud 下载的桌面应用 JSON",
        AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 100, MaxHeight = 180
    };
    private readonly TextBlock _repositoryNote = Note("");
    private readonly TextBlock _googleConfigured = Note("");
    private StackPanel _webDavFields = null!;
    private StackPanel _oauthFields = null!;
    private StackPanel _googleFields = null!;
    private Button _authorizeChannel = null!;
    private Button _deleteChannel = null!;
    private ProviderProfile? _editingProfile;
    private bool _profileDirty;

    private StackPanel BuildProfilesTab()
    {
        var panel = Stack();
        panel.Children.Add(Heading("连接云盘"));
        panel.Children.Add(_profilePick);
        var profileActions = Row();
        profileActions.Children.Add(ActionButton("添加其他渠道", async _ =>
        {
            if (!await ConfirmLeavingProfileAsync()) return;
            _editingProfile = new ProviderProfile();
            RefreshProfiles(_editingProfile.Id);
        }));
        _deleteChannel = ActionButton("删除此渠道", async _ =>
        {
            ProviderProfile profile = RequireProfile();
            bool lastChannel = _controller.Settings.Profiles.Count == 1;
            string automationNote = lastChannel && (_timer.IsOn || _onExit.IsOn) ? "同时关闭自动备份，避免继续使用已删除的渠道。" : "";
            if (await DialogAsync("删除渠道", Note($"删除“{profile.Name}”在本机保存的连接配置？云端文件和备份不会删除。{automationNote}"), "删除本机配置") != ContentDialogResult.Primary) return;
            if (lastChannel)
            {
                _controller.Settings.TimerEnabled = _timer.IsOn = false;
                _controller.Settings.OnGameExitEnabled = _onExit.IsOn = false;
            }
            _controller.Settings.Profiles.Remove(profile);
            if (_controller.Settings.ActiveProfileId == profile.Id)
                _controller.Settings.ActiveProfileId = _controller.Settings.Profiles.FirstOrDefault()?.Id;
            await _controller.SaveSettingsAsync();
            _editingProfile = null;
            ClearChannelHistory();
            RefreshProfiles(_controller.Settings.ActiveProfileId);
        }, successMessage: "已删除本机渠道配置，云端备份保留。" );
        profileActions.Children.Add(_deleteChannel);

        AddChoice(_providerKind, ProviderKind.WebDav, "WebDAV（坚果云、NAS 等）");
        AddChoice(_providerKind, ProviderKind.OneDrive, "OneDrive（微软云盘）");
        AddChoice(_providerKind, ProviderKind.GoogleDrive, "Google Drive（谷歌云盘）");
        panel.Children.Add(_providerKind);
        _webDavFields = Stack();
        foreach (UIElement field in new UIElement[] { _endpoint, _username, _password }) _webDavFields.Children.Add(field);
        _webDavFields.Children.Add(Note("坚果云等服务需使用应用专用密码。"));
        panel.Children.Add(_webDavFields);
        _oauthFields = Stack();
        _oauthFields.Children.Add(Note("填写 Microsoft Entra 桌面应用的客户端 ID，登录时会打开浏览器。"));
        _oauthFields.Children.Add(_clientId);
        panel.Children.Add(_oauthFields);
        _googleFields = Stack();
        _googleFields.Children.Add(Note("在 Google Cloud 创建 OAuth 桌面应用，下载并粘贴 JSON 配置。"));
        _googleFields.Children.Add(_googleConfigured);
        _googleFields.Children.Add(_googleJson);
        panel.Children.Add(_googleFields);

        var connectionActions = Row();
        _authorizeChannel = ActionButton("登录云盘并测试", async ct =>
        {
            await SaveProfileFieldsAsync();
            ProviderProfile profile = RequireProfile();
            await _controller.AuthorizeAsync(profile.Id, ct);
            await _controller.ProbeAsync(profile.Id, ct);
        }, successMessage: "登录成功，连接测试通过。" );
        connectionActions.Children.Add(_authorizeChannel);
        connectionActions.Children.Add(ActionButton("保存并测试连接", async ct =>
        {
            await SaveProfileFieldsAsync();
            await _controller.ProbeAsync(RequireProfile().Id, ct);
        }, successMessage: "连接测试通过。" ));
        panel.Children.Add(connectionActions);

        var advanced = Stack();
        advanced.Children.Add(profileActions);
        advanced.Children.Add(_profileName);
        advanced.Children.Add(_tenant);
        advanced.Children.Add(_repositoryNote);
        advanced.Children.Add(ActionButton("打开本机数据目录", async _ => await _controller.OpenDataFolderAsync(), trackBusy: false));
        panel.Children.Add(new Expander { Header = "更多选项", Content = advanced, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
        _profilePick.SelectionChanged += async (_, _) =>
        {
            if (_loading || Choice(_profilePick) is not Guid id || id == _editingProfile?.Id) return;
            Guid? old = _editingProfile?.Id;
            _loading = true;
            SelectChoice(_profilePick, old);
            _loading = false;
            await RunAsync(async _ =>
            {
                if (!await ConfirmLeavingProfileAsync()) return;
                ProviderProfile? selected = _controller.Settings.Profiles.FirstOrDefault(p => p.Id == id);
                if (selected == null) return;
                Guid? previousActive = _controller.Settings.ActiveProfileId;
                _controller.Settings.ActiveProfileId = selected.Id;
                try { await _controller.SaveSettingsAsync(); }
                catch { _controller.Settings.ActiveProfileId = previousActive; throw; }
                _editingProfile = selected;
                ClearChannelHistory();
                RefreshProfiles(selected.Id);
            }, false);
        };
        _providerKind.SelectionChanged += (_, _) => { UpdateProviderFields(); ProfileFieldChanged(); };
        foreach (TextBox input in new[] { _profileName, _endpoint, _username, _clientId, _tenant, _googleJson })
            input.TextChanged += (_, _) => ProfileFieldChanged();
        _password.PasswordChanged += (_, _) => ProfileFieldChanged();
        RefreshProfiles(_controller.Settings.ActiveProfileId);
        return panel;
    }

    private void ProfileFieldChanged()
    {
        if (_loading) return;
        _profileDirty = true;
        RefreshStatus();
    }

    private async Task<bool> ConfirmLeavingProfileAsync()
    {
        if (!_profileDirty) return true;
        var dialog = NewDialog("渠道信息尚未保存", Note("保存当前填写的连接信息后继续？选择放弃只会丢弃本次编辑。"), "保存并继续", "继续编辑");
        dialog.SecondaryButtonText = "放弃更改";
        ContentDialogResult result = await dialog.ShowAsync();
        if (result == ContentDialogResult.None) return false;
        if (result == ContentDialogResult.Primary) await SaveProfileFieldsAsync();
        return true;
    }

    private void ClearChannelHistory()
    {
        _historyProfileId = null;
        _snapshotList.Items.Clear();
    }

    private void RefreshProfiles(Guid? selected)
    {
        ProviderProfile? draft = _editingProfile != null && !_controller.Settings.Profiles.Any(p => p.Id == _editingProfile.Id) ? _editingProfile : null;
        if (_controller.Settings.Profiles.Count == 0 && draft == null) draft = new ProviderProfile();
        _loading = true;
        _profilePick.Items.Clear();
        foreach (ProviderProfile profile in _controller.Settings.Profiles)
            AddChoice(_profilePick, profile.Id, $"{profile.Name} · {ProviderLabel(profile.Kind)}");
        if (draft != null) AddChoice(_profilePick, draft.Id, "新渠道（待保存）");
        if (selected is Guid id) SelectChoice(_profilePick, id);
        if (_profilePick.SelectedItem == null && _profilePick.Items.Count > 0) _profilePick.SelectedIndex = 0;
        _profilePick.Visibility = _profilePick.Items.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        Guid? pick = Choice(_profilePick) as Guid?;
        ProviderProfile? chosen = _controller.Settings.Profiles.FirstOrDefault(p => p.Id == pick) ?? draft;
        _loading = false;
        LoadProfile(chosen);
        RefreshStatus();
    }

    private void LoadProfile(ProviderProfile? profile)
    {
        _editingProfile = profile;
        _loading = true;
        bool saved = profile != null && _controller.Settings.Profiles.Contains(profile);
        _profileName.Text = saved ? profile!.Name : "";
        SelectChoice(_providerKind, profile?.Kind ?? ProviderKind.WebDav);
        _endpoint.Text = profile?.Endpoint ?? "";
        _username.Text = profile?.Username ?? "";
        _clientId.Text = profile?.ClientId ?? "";
        _tenant.Text = profile?.Tenant ?? "common";
        _password.Password = "";
        _password.PlaceholderText = saved ? "留空沿用已保存密码；输入新密码可替换" : "填写服务商提供的密码";
        _googleJson.Text = "";
        _profileDirty = false;
        _loading = false;
        _deleteChannel.Visibility = saved ? Visibility.Visible : Visibility.Collapsed;
        _repositoryNote.Text = profile?.RepositoryFolder == "PotatoVN_Data" && string.IsNullOrEmpty(profile.RemotePath)
            ? "备份目录：PotatoVN_Data"
            : $"备份目录：{(string.IsNullOrEmpty(profile?.RemotePath) ? "" : profile.RemotePath.TrimEnd('/', '\\') + "/")}{profile?.RepositoryFolder}";
        _googleConfigured.Text = profile?.Kind == ProviderKind.GoogleDrive && !string.IsNullOrWhiteSpace(profile.ClientId)
            ? "已保存应用配置，可粘贴新 JSON 替换。" : "尚未导入应用配置。";
        _googleConfigured.Visibility = string.IsNullOrWhiteSpace(profile?.ClientId) ? Visibility.Collapsed : Visibility.Visible;
        UpdateProviderFields();
    }

    private void UpdateProviderFields()
    {
        ProviderKind kind = Choice(_providerKind) is ProviderKind value ? value : ProviderKind.WebDav;
        if (_webDavFields == null) return;
        _webDavFields.Visibility = kind == ProviderKind.WebDav ? Visibility.Visible : Visibility.Collapsed;
        _oauthFields.Visibility = kind == ProviderKind.OneDrive ? Visibility.Visible : Visibility.Collapsed;
        _googleFields.Visibility = kind == ProviderKind.GoogleDrive ? Visibility.Visible : Visibility.Collapsed;
        _tenant.Visibility = kind == ProviderKind.OneDrive ? Visibility.Visible : Visibility.Collapsed;
        _authorizeChannel.Visibility = kind == ProviderKind.WebDav ? Visibility.Collapsed : Visibility.Visible;
    }

    private ProviderProfile RequireProfile() => _editingProfile ?? throw new InvalidOperationException("请先填写云盘连接信息。");

    private async Task SaveProfileFieldsAsync()
    {
        ProviderProfile profile = RequireProfile();
        ProviderKind kind = Choice(_providerKind) is ProviderKind picked ? picked : ProviderKind.WebDav;
        string clientId = _clientId.Text.Trim();
        string? clientSecret = null;
        if (kind == ProviderKind.WebDav && (!Uri.TryCreate(_endpoint.Text.Trim(), UriKind.Absolute, out Uri? endpoint) ||
            (endpoint.Scheme != Uri.UriSchemeHttps && endpoint.Scheme != Uri.UriSchemeHttp) ||
            !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment)))
            throw new InvalidOperationException("请填写服务商提供的 HTTP(S) WebDAV 地址；账号和密码请填在各自的输入框中。");
        if (kind == ProviderKind.GoogleDrive && !string.IsNullOrWhiteSpace(_googleJson.Text))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(_googleJson.Text);
                if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("installed", out JsonElement installed) ||
                    installed.ValueKind != JsonValueKind.Object || !installed.TryGetProperty("client_id", out JsonElement id) || id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString()))
                    throw new InvalidOperationException("请使用 Google Cloud 下载的“桌面应用”OAuth JSON 配置。");
                clientId = id.GetString()!;
                if (!installed.TryGetProperty("client_secret", out JsonElement secret) || secret.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(secret.GetString()))
                    throw new InvalidOperationException("Google 配置缺少客户端密码，请完整粘贴下载的桌面应用 JSON。");
                clientSecret = secret.GetString();
            }
            catch (JsonException) { throw new InvalidOperationException("Google 配置不是有效 JSON，请完整粘贴下载文件的内容。"); }
        }
        if (kind == ProviderKind.GoogleDrive && string.IsNullOrWhiteSpace(_googleJson.Text) && profile.Kind != ProviderKind.GoogleDrive)
            throw new InvalidOperationException("切换到 Google Drive 时，请粘贴对应的桌面应用 JSON 配置。");
        if (kind != ProviderKind.WebDav && string.IsNullOrWhiteSpace(clientId))
            throw new InvalidOperationException(kind == ProviderKind.GoogleDrive ? "请先粘贴 Google Cloud 的桌面应用 JSON 配置。" : "请先填写 Microsoft Entra 的应用程序（客户端）ID。");
        profile.Name = string.IsNullOrWhiteSpace(_profileName.Text) ? ProviderLabel(kind) : _profileName.Text.Trim();
        profile.Kind = kind;
        profile.Endpoint = _endpoint.Text.Trim();
        profile.Username = _username.Text.Trim();
        profile.ClientId = clientId;
        profile.Tenant = string.IsNullOrWhiteSpace(_tenant.Text) ? "common" : _tenant.Text.Trim();
        if (!_controller.Settings.Profiles.Contains(profile)) _controller.Settings.Profiles.Add(profile);
        if (!string.IsNullOrEmpty(_password.Password)) await _controller.SetSecretAsync(profile.Id, "password", _password.Password);
        if (clientSecret != null) await _controller.SetSecretAsync(profile.Id, "client-secret", clientSecret);
        bool changedChannel = _profileDirty || _controller.Settings.ActiveProfileId != profile.Id;
        _controller.Settings.ActiveProfileId = profile.Id;
        await _controller.SaveSettingsAsync();
        if (changedChannel) ClearChannelHistory();
        RefreshProfiles(profile.Id);
    }
}
