# 验证记录

日期：2026-11-10（Asia/Shanghai）。所有自动检查使用隔离合成资料和注入的模拟 HTTP 处理器，没有上传用户游戏数据，也没有操作真实云盘账号。

## 已验证

- `PotatoVN.Backup.Tests`：**23 项通过**。选择性字段和图片、非本地条目、部分合并、游戏匹配冲突、分类/主页引用、库延后映射、多位置存档、重复恢复和回退、ZIP 路径边界、损坏备份、配置损坏、待恢复区持久化、共享引用清理、忙碌项目保护、空文件保护、清单提交顺序和历史引用保留。
- `tests/ProviderProtocolChecks`：13 项通过。覆盖 WebDAV 往返和 Unicode、两种 OAuth 刷新、OneDrive/Google 分片及分页、签名传输不携带 Bearer、同名文件冲突、真实 Windows DPAPI 跨实例读取删除，以及协调器的跨设备引用、位置规则变化、丢失存档包、引用保护和非法清单。
- `tests/RestoreMatchingChecks`：**31 项通过**。恢复前未匹配本地条目预览、一对一选择、历史别名、空设备同来源不同版本保持独立、不关联决定持久化、非本地条目与待恢复存档、仅设置恢复跳过游戏、重复资料合并及库/分类/制作人员/主页引用迁移、仅存档恢复的资料合并、下载损坏与取消不提交映射、重新关联清理旧目标路径、同目标刷新保留用户路径，以及宿主拒绝导入时不提交映射。
- WinUI 主插件 x64 Release 已成功编译并生成 `.pvnplugin.zip`，编译未报告警告或错误。最终包 924,012 字节、31 个文件项，ZIP CRC 全部通过；主 DLL、核心 DLL 和 PRI 与本轮编译产物的 SHA-256 一致，共享宿主程序集未打入插件包。
  安装包 SHA-256：`F262D082010918D1B60C4037D3C67DB88A6DF471A628552E40F05C720E14DDD6`。

## 宿主兼容处理

源代码审查确认原生导出会遗漏已迁移至 LiteDB `homepage_list` 的布局，因此兼容层补充该数据，并在导入数据库中恢复。

导入文件先复制为不匹配宿主导入扩展名的临时文件，完整后同目录原子发布。重启导入保留本机插件数据库记录和安装文件。未选的游戏资料和已关联路径从当前原生导出合并保留。

另发现非打包宿主的一种默认布局将临时目录放在数据目录内部，原生导入会尝试把临时目录移动到自身子树。插件对这种布局使用只在确认恢复后运行的隐藏重启器，给下个宿主进程指定独立临时目录。没有更改系统或用户级环境变量。外部重启器配置损坏或宿主程序不存在时，两项合成负向检查通过：已发布导入包先改为宿主不会识别的扩展名，再撤回本次暂存目录，保留副本及错误说明；没有留下普通启动会误导入的文件。脚本已通过 PowerShell 语法解析，实际宿主成功重启路径仍待现场验收。

## 待现场验收

- WebDAV 服务商差异；微软个人/组织账号和谷歌账号的真实浏览器登录、服务端策略、配额及长时间令牌刷新。
- 真实 PotatoVN 中的安装、重载、软件数据重启恢复、插件记录保留、主页数据库导入以及隐藏重启器。
- 游戏实际读写存档、不同发行版本之间的存档兼容性。

独立界面检查只用于验证控件构造与布局，不能替代上述真实宿主和账号验收。未执行任何对用户真实数据目录的恢复试验。

## 本机持久化与界面检查

- `tests/FilePersistenceChecks`：通过 1,500 次连续原子配置写入及读取；验证允许删除共享的旧读取句柄仍看到完整旧版本，新读取看到新版本；取消写入不改变已提交数据，也不残留临时文件。
- 在这台 Windows 上曾复现 `File.Move` 覆盖拒绝访问，以及 `File.Replace` 返回 1175。最终使用 Windows 10 原子重命名的 POSIX 替换语义，支持长路径和 UNC，不靠延迟重试。仍会尊重其他进程拒绝删除共享的显式文件锁，遇到这种锁保留旧数据并报告错误。
- `tests/UiSmoke`：独立 WinUI 测试窗，不引用真实宿主、不使用真实游戏库或云盘。六个页面均完成加载、布局和 PNG 导出，并断言底部操作按钮在高 DPI 窗口内可见。已修复固定最小高度导致的裁切。
- 新增恢复对应关系弹窗的 8 项隔离界面检查通过：仅设置和全部明确匹配时不弹窗、预取消、两条未匹配行实际渲染、首次关闭、第二次合并确认的取消和关闭，以及无配置/下载/恢复副作用。修复 WinRT 原生 `Hide` 方法组在取消回调中触发 `InvalidCastException` 的问题。
- 测试窗的合成数据与截图位于 `tests/UiSmoke/artifacts/matching-pass/`（本轮六个页面、关联确认与合并确认），仅用于展示和布局检查；不能替代真实账号和宿主重启恢复验收。

```powershell
dotnet run --project tests/RestoreMatchingChecks/RestoreMatchingChecks.csproj
dotnet run --project tests/FilePersistenceChecks/FilePersistenceChecks.csproj
msbuild tests/UiSmoke/UiSmoke.csproj /restore /p:Configuration=Release /p:Platform=x64
```

Windows 文件重命名规则参考：[FILE_RENAME_INFORMATION](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntifs/ns-ntifs-_file_rename_information)。
