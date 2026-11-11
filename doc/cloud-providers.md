# 云端渠道配置

三种渠道均由插件直接上传和下载，不需要安装网盘同步客户端。新备份采用文件级增量，变化文件独立压缩上传，未变化对象跨版本复用。每个配置拥有独立的凭据，选择的渠道直接用于备份与恢复，切换前会检查未保存的编辑。新渠道的备份目录固定为 `PotatoVN_Data`，连接测试时自动创建，不需要用户配置。旧配置保留原有父目录和 `PotatoVN-DataBackup`，继续访问原备份。

## WebDAV

填写以下信息：

- **服务器地址**：服务商提供的 WebDAV HTTP(S) 地址，包含服务入口路径；建议使用 HTTPS。
- **用户名／密码**：使用服务商要求的应用专用密码。留空用户名时按匿名访问处理。

点击“保存并测试连接”即可保存并验证；WebDAV 不需要额外登录或授权。

例如，服务器地址为 `https://example.com/remote.php/dav/files/alice/`，插件会在该入口下创建 `PotatoVN_Data/v1/`。示例地址不对应真实账号。

连接检查会创建插件自己的目录并检查能否读取目录。服务端需允许 `PROPFIND`、`MKCOL`、`PUT`、`GET` 和 `DELETE`。

## OneDrive

1. 在 [Microsoft Entra 管理中心](https://entra.microsoft.com/) 注册一个应用。
2. 根据自己的账号选择支持的账号类型。需要个人微软账号时，应包含“个人 Microsoft 账户”；同时使用工作／学校账号时选择相应多租户账号类型。
3. 在身份验证中添加“移动和桌面应用”平台，配置重定向 URI：`http://localhost`。插件会使用临时端口，这是微软对桌面客户端支持的方式。
4. 添加 Microsoft Graph **委托权限** `Files.ReadWrite.AppFolder`。插件只访问该应用专用目录，不需要整盘读写权限。
5. 将“应用程序（客户端）ID”填写到插件。“更多选项”中的微软登录目录默认为 `common`；只使用个人账号可填 `consumers`，组织环境可填写自己的租户 ID。
6. 点击“登录云盘并测试”，在系统浏览器完成授权，随后自动检查连接。

桌面应用不使用微软客户端密码。某些组织会禁止用户自行授予权限，此时需要组织管理员按其策略处理。

备份位于 OneDrive 的 `Apps/<注册的应用名称>/` 下面，再自动建立 `PotatoVN_Data`。换设备时应沿用同一个应用 ID 和微软账号；更换应用 ID 会对应另一个应用目录。

参考：[OneDrive 应用专用目录](https://learn.microsoft.com/en-us/graph/onedrive-sharepoint-appfolder)、[桌面应用系统浏览器登录](https://learn.microsoft.com/en-us/entra/msal/dotnet/acquiring-tokens/using-web-browsers)。

## Google Drive

1. 在 [Google Cloud 控制台](https://console.cloud.google.com/) 创建或选择自己的项目，并启用 Google Drive API。
2. 配置 OAuth 同意屏幕。自用测试时，将自己的 Google 账号加入测试用户。
3. 创建 **Desktop app／桌面应用** 类型的 OAuth 客户端，并下载客户端 JSON 配置。
4. 在插件粘贴完整 JSON，保存或登录时自动导入，无需再点导入按钮。桌面应用属于公共客户端，这个客户端密码不等于 Google 账号密码。
5. 点击“登录云盘并测试”，在系统浏览器授权。插件仅请求 `https://www.googleapis.com/auth/drive.file`，用于管理由本应用创建的备份文件。
6. 授权后自动检查连接。插件在“我的云端硬盘”下自动创建 `PotatoVN_Data`，不会使用已有文件夹的名称来推断用户对整盘的授权。

新设备应使用同一 Google 账号和同一 OAuth 客户端配置。`drive.file` 的可见范围与应用授权有关。

Google 将外部应用保持在 **Testing／测试** 发布状态时，Drive 授权的刷新令牌通常会在 7 天后失效。长期自动备份应按 Google 控制台要求调整应用发布配置；令牌被撤销或失效时，插件会提示重新登录，后台任务不会自行弹出登录浏览器。

如果 Google Drive 返回同名目录／文件冲突，插件会停止操作，避免擅自选择另一份仓库。请在云盘中检查重复项目并保留正确的备份目录，再重试；插件不会自动搬移旧备份。

参考：[桌面应用 OAuth](https://developers.google.com/identity/protocols/oauth2/native-app)、[Drive 权限范围](https://developers.google.com/workspace/drive/api/guides/api-specific-auth)、[刷新令牌有效期](https://developers.google.com/identity/protocols/oauth2#expiration)。

## 凭据与传输

- 微软／谷歌使用系统浏览器、授权码和 PKCE；回调仅监听本机 loopback 地址，有 5 分钟有效期。
- 密码、客户端密码和令牌使用 Windows 当前用户 DPAPI 加密保存，不写入云端备份包。复制这些加密文件到另一个 Windows 用户或设备不能替代重新登录。
- 文件流式上传和下载；OneDrive 大文件使用 10 MiB 分片，Google Drive 使用 8 MiB 分片。
- 网络中断或取消会使当前传输失败，不伪装成已完成备份。首版不保存跨应用重启的上传会话，需要重新发起失败任务。
- 对象与清单使用最后写入的 `.complete` 提交标记，半上传内容不会被当成成功版本；文件级复用不依赖服务商是否支持差分上传或原子重命名。
- 云端历史删除只针对本设备明确过期、且没有保留引用的插件备份文件；共享对象保留引用闭包不完整时停止清理。清理本机待恢复区不会调用云端删除接口。

## 开发验证

`tests/ProviderProtocolChecks` 使用注入的 HTTP 处理器验证真实请求格式与响应处理，不联网，也不会打开账号登录页面：

```powershell
dotnet run --project tests/ProviderProtocolChecks/ProviderProtocolChecks.csproj
```

覆盖 WebDAV 文件往返和路径处理、OAuth 过期及 HTTP 401 刷新、OneDrive 分片与预授权 URL、Google Drive 分片及分页、同名项目冲突，以及 Windows DPAPI 跨实例读取和删除。真实账号登录、组织策略与各服务商兼容性仍需实际环境验收。

同一验证项目还覆盖协调器的 7 项仓库边界：清单最后发布、未变化存档去重、上传失败不提交、未关联新设备沿用旧设备存档引用、位置规则变化产生新版本、缺失存档包不被继续复用、其他设备保留快照引用保护，以及非法远端路径和 null 清单拒绝。新旧渠道目录兼容及配置序列化往返也纳入回归。当前合计 14 项模拟协议／协调器检查。
