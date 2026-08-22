# TermSearch 术语速查

一个 Windows 常驻小工具：按下全局热键，弹出输入框，输入缩写立即查到全称和解释。查不到时可以在同一个窗口里直接补充，写回本地 `terms.json`。

详细的需求规格、交互细节、设计取舍见 [require.md](require.md)。本文档只讲"怎么用、怎么编译"。

## 功能一览

- 全局热键唤起（默认 `Alt+Space`，可在托盘菜单里改，或直接改 `config.json`）
- 大小写不敏感的前缀匹配，一个缩写支持多条释义（比如 `PM` 同时对应 Product Manager / Project Manager）
- 查不到时提示"暂未添加，待补充"，按 Enter 直接在弹窗里补充全称和解释，保存后立刻生效
- 结果按 Enter 复制全称到剪贴板并关闭；Esc / 点击窗口外关闭
- 术语表 `terms.json` 支持外部编辑后自动热重载，无需重启程序
- 托盘图标：打开术语表文件、重新加载、修改热键、开机自启动、退出
- 单实例运行，绿色版单文件 exe，不需要安装

## 快速开始

已经编译好的绿色版在 [src/TermSearch/bin/Release/net9.0-windows/win-x64/publish/](src/TermSearch/bin/Release/net9.0-windows/win-x64/publish/)：把这个文件夹整个拷到任意位置，双击 `TermSearch.exe` 即可，无需安装、无需管理员权限。

首次使用：

1. 按 `Alt+Space` 唤起输入框
2. 输入缩写，比如 `API`
3. 看到结果后按 `Enter` 复制全称，或按 `Esc` 关闭
4. 查不到会提示"暂未添加，待补充"，按 `Enter` 就地补充全称/解释并保存

## 开发 / 编译

需要 .NET 9 SDK。

```powershell
cd src\TermSearch

# 调试运行
dotnet build

# 生成绿色版单文件 exe（自包含，win-x64，输出在 bin\Release\net9.0-windows\win-x64\publish\）
dotnet publish -c Release
```

项目结构：

```
require.md              需求规格文档
src/TermSearch/          WPF 主程序（.NET 9）
  App.xaml(.cs)           启动、单实例、托盘/热键/术语表的装配
  PopupWindow.xaml(.cs)    核心交互窗口：查询、结果、快捷补充
  HotkeySettingWindow.*    托盘菜单里"修改全局热键"的小对话框
  Models/                  TermEntry、AppConfig 数据模型
  Services/                热键注册、术语表读写与热重载、托盘图标、开机自启动、日志
  terms.json               术语数据（随程序目录分发，绿色版思路）
  config.json              热键、开机自启动等配置
tools/                   数据整理用的 PowerShell 脚本（见下）
```

## 术语数据格式

`terms.json` 是 `{ 缩写: [ { FullName, Description }, ... ] }`，字段名大小写敏感，必须是 `FullName`/`Description`：

```json
{
  "API": [
    { "FullName": "Application Programming Interface", "Description": "应用程序编程接口" }
  ],
  "PM": [
    { "FullName": "Product Manager", "Description": "产品经理" },
    { "FullName": "Project Manager", "Description": "项目经理" }
  ]
}
```

直接用文本编辑器改这个文件保存即可，程序会自动感知变化并重新加载。

## 工具脚本（`tools/`）

两个 PowerShell 脚本，用来批量整理术语数据，跟主程序本身无关，手动按需运行。

### `Convert-TmpToTerms.ps1`

把另一种收集格式（每个缩写一个对象，单释义直接给 `fullname`/`description`，多释义用 `include` 数组）整体转换成 `terms.json` 的格式。

```powershell
tools\Convert-TmpToTerms.ps1 -InputPath 源文件.json -OutputPath 输出文件.json
```

默认不带参数时读项目根目录的 `tmp.json`，写到 `terms.converted.json`（不会动 `src\TermSearch\terms.json`，确认结果无误后自己决定要不要覆盖）。

### `Merge-TermsFromText.ps1`

把"缩略语~全称~描述"格式的文本文件（每行一条）增量合并进已有的 `terms.json`：缩写不存在就新增；缩写存在但全称不同就在同一缩写下追加一条（多义词）；缩写和全称都完全一致就跳过，不会产生重复。只增不删，不会动已有数据。

```powershell
tools\Merge-TermsFromText.ps1 -InputPath 词条.txt -OutputPath src\TermSearch\terms.json
```

两个脚本都用大小写敏感的方式解析/合并 JSON（不是 PowerShell 自带的 `ConvertFrom-Json`），因为缩写本身可能存在只有大小写不同的两个不同词条（比如 `COT` 和 `CoT`）。
