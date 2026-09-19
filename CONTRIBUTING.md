# 参与开发

先说结论：这是个刻意做「小」的工具，**只有两条硬约束**，其余都好商量。

## 两条硬约束

1. **不引入任何第三方依赖**。只用 Windows 自带的东西（.NET Framework 4.x 的 BCL、WinForms、
   `System.Security.Cryptography`），编译只用系统自带的 `csc.exe`。
   不引 NuGet 包，不要求用户装 .NET SDK / Visual Studio / 运行库。
2. **源码保持 C# 5 语法**。因为系统自带的 `csc.exe`（`Framework64\v4.0.30319`，C# 5 编译器）
   要能直接编。具体就是不要用：
   - 字符串插值 `$"..."`（用 `+` 或 `string.Format`）
   - null 条件运算符 `?.`、`??=`、`out var`、元组、模式匹配
   - 局部函数、表达式体成员（`=> ...`）、自动属性初始化器
   - `nameof`、`using static`、`async/await`（本项目的并发用 `Thread` + `ConcurrentQueue`）

   这些新语法在 `dotnet build` / 新版 Roslyn 下当然能编，但那样就丢掉了「不装任何东西」这个卖点。

## 目录结构

```
src/HashTool.cs       全部源码（界面 + 扫描 + 哈希 + JSON + 自检），单文件，约 2400 行
src/app.ico           程序图标
src/app.manifest      高 DPI 感知 + 普通权限运行
tools/make-icon.ps1   重新生成图标（以及 docs/icon.png 预览图）
build.bat             编译（产物 dist/HashTool.exe）
docs/                 README 用的图片
```

## 本地开发流程

```bat
:: 1. 改 src\HashTool.cs
:: 2. 编译（双击 build.bat 也行）
build.bat

:: 3. 自检：哈希已知向量 + 界面构造 + 递归扫描
dist\HashTool.exe --selftest

:: 4. 界面自测：13 项断言，覆盖增量刷新和 Ctrl+C 复制
dist\HashTool.exe --uitest .\ui_out

:: 5. 改了界面的话，重新生成 README 截图
dist\HashTool.exe --screenshot docs\screenshot.png
```

改了行为就顺手加一条 `--uitest` 断言（在 `Program.RunUiTest` 里），这样以后别人改坏会被 CI 拦住。

## 提交 Pull Request 之前

- `build.bat` 编译无警告（本项目把 CS0649 这类"字段声明了没赋值"的警告当错误，防止空引用）
- `--selftest` 和 `--uitest` 都通过
- 动过界面就附上新的 `--screenshot` 截图
- 用中文或英文写提交信息都可以，说清楚"为什么"比"改了什么"更重要

## 发布流程（维护者）

1. **改版本号：只改 `src/HashTool.cs` 里的 `Const.Version` 一行**（比如改成 `"1.2.0"`）。
   窗口标题、`--version`、导出的 JSON、自检输出、程序集版本全都从它派生，别处不用动。
   （exe 属性里「文件版本」显示 `1.2.0.0` 是 .NET 强制 4 段自动补的，
   「产品版本」显示 `1.2.0`，和 tag 一致。）
2. 在 `CHANGELOG.md` 里把「未发布」那一节整理成本次版本号。
3. 提交：`git commit -m "release: v1.2.0"`
4. 打 tag 并推送：`git tag v1.2.0 && git push origin v1.2.0`
   （tag 名必须和 `Const.Version` 一致，只是前面多个 `v`）
5. GitHub Actions 会自动编译、自检、校验版本号、把 `HashTool.exe` 和它的 SHA-256 附到 Release 上

> **版本号写漏了会怎样？** Release 流程里有一道检查：拿 tag 和 exe 自己报的 `--version` 比对，
> 不一致就直接失败并提示你改 `Const.Version`，所以不会出现「tag 是 v1.2.0、程序里还写着 1.1.0」这种事。

`release.yml` 只在推 `v*` 的 tag 时触发，平时的提交只会跑 `build.yml`（编译 + 自检，不发布）。
想删掉打错的 tag：`git tag -d v1.2.0 && git push origin :refs/tags/v1.2.0`。

## 搭建这个仓库时做了什么（以后照着来）

### 环境事实

| 项目 | 当时的情况 |
| --- | --- |
| 编译器 | `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`（系统自带，C# 5 编译器） |
| Visual Studio / .NET SDK | **没装，也不需要**（所以故意没有 .sln / .csproj） |
| 构建方式 | `build.bat` 里那一行 `csc` 命令就是全部构建逻辑 |
| CI | GitHub Actions `windows-latest`，自带的 .NET Framework 足够编译 |
| git 身份 | `wangxiaobao <2935601977@qq.com>`（写在仓库的 `.git/config` 里，只影响本仓库） |
| 远端 | `git@github.com:2935601977/HashTool.git`（SSH，`~/.ssh/id_ed25519`） |

### 踩过的坑（照着避开，能省不少时间）

1. **PowerShell 脚本的编码**：Windows PowerShell 5.1 会把「没有 BOM 的 UTF-8 的 .ps1」
   当成 GBK 读，中文注释直接导致语法错误。给 .ps1 加 UTF-8 BOM，或者把脚本写成纯 ASCII。
2. **执行策略**：`.\xxx.ps1` 可能被 "禁止运行脚本" 拦下。用
   `powershell -NoProfile -ExecutionPolicy Bypass -File xxx.ps1`，或者干脆用 `build.bat`。
3. **csc 只吃 C# 5 语法**：字符串插值、`?.`、`out var`、局部函数、表达式体成员一律编译不过。
4. **`/warnaserror` 要写数字**：`/warnaserror+:649`（不能写 `CS0649`，编译器不认）。
5. **源码编码**：编译时要带 `/codepage:65001`，否则中文字符串会乱码。
6. **Workflow 的 step 里只能用白名单键**（`name`/`run`/`shell`/`if`/`uses` 等）。
   我曾在里面写了 `rem ...` 当注释——那是批处理写法，会让整个 workflow 判定为无效，
   YAML 注释要用 `#`。
7. **`--uitest` 默认会删掉测试目录**。CI 里要用它生成的文件做交叉校验，所以加了 `--keep`，
   校验完再用 `if: always()` 的步骤清理。
8. **.NET 的 `Icon` 类解不了 PNG 压缩的图标帧**，所以 `tools/make-icon.ps1` 里
   小尺寸（16–64）用传统 DIB 帧、大尺寸（128/256）才用 PNG 帧。改图标时别把这段改坏。
9. **控制台中文**：输出被重定向到管道/文件时，要用 UTF-8 的 `StreamWriter` 包一层，
   否则中文在管道里会变成乱码（见 `Cli.SetupConsoleEncoding`）。
10. **提交中文信息**：PowerShell 往 git 传中文参数容易被转码，
    稳妥做法是把信息写进一个 UTF-8（无 BOM）的临时文件，然后 `git commit -F 文件`。

### 日常改动流程（记住这五步就行）

```bat
:: 1. 改 src\HashTool.cs（只改这一个文件）
:: 2. 编译
build.bat

:: 3. 自检
dist\HashTool.exe --selftest
dist\HashTool.exe --uitest .\ui_out

:: 4. 动了界面就更新 README 截图
dist\HashTool.exe --screenshot docs\screenshot.png

:: 5. 提交推送，等 CI 变绿
git add -A && git commit -m "fix: ..." && git push
```

CI 里已经包含：编译、`--selftest`、`--uitest`（13 项断言）、JSON 结构校验、
**与系统 `Get-FileHash` 逐文件交叉校验**、生成界面截图并作为产物上传。
推送后到仓库的 Actions 页面就能看到这轮的 exe 和截图。
